using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nebula.Configuration;
using Nebula.Game;
using Nebula.Modules;
using Nebula.Roles.Assignment;
using Nebula.SpecialModes.AeroGuesser;
using UnityEngine;
using Virial;
using Virial.Attributes;
using Virial.DI;
using Virial.Events.Game;
using Virial.Events.Game.Meeting;
using Virial.Events.Player;
using Virial.Game;
using Virial.Runtime;
using Virial.Utilities;
using GamePlayer = Virial.Game.Player;

namespace PsycoWheel;

/// <summary>
/// キチガイ風車モードのゲームで生成されるモジュールです。
/// </summary>
public interface IGameModePsychoWheel : IGameModeModule
{
}

/// <summary>
/// キチガイ風車モードです。
/// 全員がタスクを持たないクルーメイトになり、会議は開かれません。
/// 制限時間の間に歩き回る風車を「見た」回数を競います。
/// </summary>
//ゲームモードはRolesフェーズより前に登録する必要がある
[NebulaPreprocess(PreprocessPhase.PostLoadAddons)]
public class PsychoWheelGameMode : AbstractModuleContainer, IModule, IGameModePsychoWheel, IGameOperator
{
    static public GameModeDefinition Definition { get; private set; } = null!;

    static void Preprocess(NebulaPreprocessor preprocessor)
    {
        Definition = preprocessor.RegisterGameMode("gamemode.psychoWheel", 0, typeof(IGameModePsychoWheel), () => new FreePlayRoleAllocator(), false);
        preprocessor.DIManager.RegisterContainer(() => new PsychoWheelGameMode());
    }

    /// <summary>
    /// ロビーで選ばれている、または現在のゲームのゲームモードがキチガイ風車モードの場合、trueを返します。
    /// </summary>
    static public bool IsSelected => GeneralConfigurations.CurrentGameMode == Definition;

    /// <summary>
    /// 現在のゲームがキチガイ風車モードの場合、そのモジュールを返します。
    /// </summary>
    static public PsychoWheelGameMode? Current => NebulaAPI.CurrentGame?.GameMode as PsychoWheelGameMode;

    private const float ScoreSyncInterval = 0.5f; //自分の回数が変わったときに送る間隔。累計を送るので、間の送信はまとめてよい
    private const float FinalSyncDelay = 1.5f; //終了を決めてから、最後の回数が届くのを待つ時間
    private const float TimerZ = -20f;
    private const float DoorCloseInterval = 10f; //ホストがドアをランダムに閉める間隔

    //ゲームモードの設定
    bool IGameModeModule.AllowSpecialGameEnd => false;
    bool IGameModeModule.CanGetTitle => false;
    bool IGameModeModule.AllowPerks => false; //会議が無く植物が育たないので、植物もパーク枠も出さない

    //各プレイヤーが「見た」回数。各自が自分の累計を送る
    static private readonly RemoteProcess<(GamePlayer player, int score)> RpcScore = new("PsychoWheelScore", (message, _) =>
        Current?.SetScore(message.player, message.score));

    //ホストが選んだドアを全員で閉める。部屋単位で閉める場合はroomに部屋(SystemTypes)を、ドア1つを閉める場合はShipStatus.AllDoorsの添字を渡す
    static private readonly RemoteProcess<(bool byRoom, int value)> RpcCloseDoors = new("PsychoWheelCloseDoors", (message, _) =>
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        if (message.byRoom)
        {
            ship.CloseDoorsOfType((SystemTypes)message.value);
        }
        else if (message.value >= 0 && message.value < ship.AllDoors.Length)
        {
            ship.AllDoors[message.value].SetDoorway(false);
        }
    });

    //ホストが制限時間の計測を始めた
    static private readonly RemoteProcess<float> RpcStartTimer = new("PsychoWheelStartTimer", (duration, _) =>
        Current?.StartTimer(duration));

    private readonly Dictionary<byte, int> scores = [];
    private int localScore = 0;
    private bool localScoreDirty = false;
    private float scoreSyncTimer = 0f;

    private object? timerDisplay = null; //NoSの内部クラスのTimer。フィールドの型には内部クラスを使えないのでobjectで持つ
    private float? remainingTime = null; //計測を始めるまではnull
    private float timeLimit = 0f;
    private bool waitingForStart = true; //ホストが、スポーン位置の選択が終わるのを待っている間だけtrue
    private float? endTimer = null; //ホストが終了を決めてから値を持つ

    //ドアをランダムに閉める(ホストだけが使う)
    private float doorCloseTimer = DoorCloseInterval;
    private bool doorTargetsPrepared = false;
    private bool closeDoorsByRoom = false; //自動で開くドアのマップ(The Skeld)では部屋単位で閉める
    private SystemTypes[] doorRooms = []; //部屋単位で閉める場合の、ドアのある部屋の一覧

    public PsychoWheelGameMode()
    {
        this.RegisterPermanently();
    }

    /// <summary>
    /// 制限時間が過ぎた場合、trueを返します。以降は「見た」回数を数えません。
    /// </summary>
    public bool IsTimeUp => remainingTime <= 0f;

    /// <summary>
    /// 制限時間の計測中の場合、trueを返します。
    /// </summary>
    public bool IsInProgress => remainingTime > 0f;

    /// <summary>
    /// 制限時間の計測を始めてからの時間です。始まる前は0です。
    /// </summary>
    public float ElapsedTime => remainingTime.HasValue ? timeLimit - remainingTime.Value : 0f;

    public int LocalScore => localScore;

    private int GetScore(GamePlayer player) => scores.TryGetValue(player.PlayerId, out var score) ? score : 0;

    private void SetScore(GamePlayer player, int score)
    {
        if (player == null) return;
        scores[player.PlayerId] = score;
    }

    /// <summary>
    /// 自分が風車を見た回数を1つ増やします。
    /// </summary>
    public void AddLocalScore()
    {
        if (!IsInProgress) return;
        localScore++;
        localScoreDirty = true;
        //自分の画面の表示はすぐに更新する
        if (NebulaAPI.CurrentGame?.LocalPlayer is { } localPlayer) SetScore(localPlayer, localScore);
    }

    private void StartTimer(float duration)
    {
        remainingTime = duration;
        timeLimit = duration;
        var timer = (timerDisplay ??= new Timer(TimerZ)) as Timer;
        timer!.SetTimer(Arithmetic.Timer(duration));
        timer.SetActive(true);
    }

    /// <summary>
    /// 残り時間を00:00にして、表示を上にはけさせます。
    /// </summary>
    private void HideTimer()
    {
        if (timerDisplay is not Timer timer) return;
        //Timerは残り秒数に1を足して切り捨てた値を表示するため、0のままでは00:01になる。0未満の値を渡して00:00にする
        timer.SetTimer(Arithmetic.Constant(-0.5f));
        timer.SetActive(false);
    }

    //通報も緊急会議もできない
    void OnGameStart(GameStartEvent ev)
    {
        if (NebulaAPI.CurrentGame?.LocalPlayer is { } localPlayer) localPlayer.AttachAbility(new NoMeetingAbility(localPlayer));
    }

    void OnCheckEmergencyButton(CheckCanPushEmergencyButtonEvent ev) => ev.DenyButton(null);

    //会議が開かれないので、死亡したらすぐに天界視点(観戦)のアビリティを得る。通常は会議の終了時に得る
    void OnPlayerDie(PlayerDieEvent ev)
    {
        if (ev.Player.AmOwner) NebulaGameManager.Instance?.ChangeToSpectator();
    }

    void OnGameUpdate(GameUpdateEvent ev)
    {
        UpdateScoreSync(ev.DeltaTime);

        if (remainingTime > 0f)
        {
            remainingTime = Mathf.Max(0f, remainingTime.Value - ev.DeltaTime);
            if (IsTimeUp) HideTimer();
        }

        if (!AmongUsClient.Instance.AmHost) return;

        //ゲーム開始時のスポーンの待機が終わってから計測を始める(PsychoWheelManagerと同じ判断)
        if (waitingForStart)
        {
            if (HudManager.Instance.IsIntroDisplayed) return;
            waitingForStart = false;
            RpcStartTimer.Invoke(PsychoWheelOptions.TimeLimitOption.GetValue());
            return;
        }

        if (IsInProgress) UpdateDoorClosing(ev.DeltaTime);

        if (endTimer == null)
        {
            //制限時間が過ぎるか、全員が死亡したら終了する
            if (IsTimeUp || GamePlayer.AllPlayers.All(p => p.IsDead || p.IsDisconnected)) endTimer = FinalSyncDelay;
            return;
        }

        endTimer -= ev.DeltaTime;
        if (endTimer > 0f) return;
        endTimer = float.MaxValue; //二重に終了させない
        EndGame();
    }

    /// <summary>
    /// ホストとして、一定間隔でドアをランダムに閉めます。
    /// </summary>
    private void UpdateDoorClosing(float deltaTime)
    {
        var ship = ShipStatus.Instance;
        if (!ship) return;
        PrepareDoorTargets(ship);

        doorCloseTimer -= deltaTime;
        if (doorCloseTimer > 0f) return;
        doorCloseTimer += DoorCloseInterval;

        if (closeDoorsByRoom)
        {
            //開いているドアがある部屋から選ぶ
            var rooms = doorRooms.Where(room => ship.AllDoors.Any(door => door.Room == room && door.IsOpen)).ToArray();
            if (rooms.Length == 0) return;
            RpcCloseDoors.Invoke((true, (int)rooms[Random.Range(0, rooms.Length)]));
        }
        else
        {
            //ドアが無いマップ(MIRA HQ)では何もしない
            var openDoors = Enumerable.Range(0, ship.AllDoors.Length).Where(i => ship.AllDoors[i].IsOpen).ToArray();
            if (openDoors.Length == 0) return;
            RpcCloseDoors.Invoke((false, openDoors[Random.Range(0, openDoors.Length)]));
        }
    }

    /// <summary>
    /// マップのドアの仕組みを調べ、部屋単位で閉める場合は、ドアのある部屋を洗い出しておきます。
    /// </summary>
    private void PrepareDoorTargets(ShipStatus ship)
    {
        if (doorTargetsPrepared) return;
        doorTargetsPrepared = true;

        //The Skeldのドアは自動で開く(AutoDoorsSystemType)。ほかのマップのドアは手で開ける(DoorsSystemType)
        closeDoorsByRoom = ship.Systems.TryGetValue(SystemTypes.Doors, out var doorSystem) && doorSystem.TryCast<AutoDoorsSystemType>() != null;
        if (closeDoorsByRoom) doorRooms = ship.AllDoors.Select(door => door.Room).Distinct().ToArray();
    }

    private void UpdateScoreSync(float deltaTime)
    {
        scoreSyncTimer -= deltaTime;
        //制限時間が過ぎたら、待たずに最後の回数を送る
        if (!localScoreDirty || (scoreSyncTimer > 0f && !IsTimeUp)) return;
        if (NebulaAPI.CurrentGame?.LocalPlayer is not { } localPlayer) return;

        localScoreDirty = false;
        scoreSyncTimer = ScoreSyncInterval;
        RpcScore.Invoke((localPlayer, localScore));
    }

    private void EndGame()
    {
        int winnersMask = 0;
        var orderedPlayers = ScoreOrderedPlayers;
        if (orderedPlayers.Length > 0)
        {
            int maxScore = orderedPlayers[0].score;
            foreach (var entry in orderedPlayers)
            {
                if (entry.score < maxScore) break;
                winnersMask |= 1 << entry.player.PlayerId;
            }
        }
        NebulaGameManager.Instance?.RpcInvokeForciblyWin(NebulaGameEnd.GameEnd, winnersMask);
    }

    //結果画面の表示はアモゲッサーにならう
    static private readonly UnityEngine.Color[] RankColor = [new(1f, 196f / 255f, 0f), new(170f / 255f, 180f / 255f, 186f / 255f), new(193f / 255f, 119f / 255f, 31f / 255f)];

    private (GamePlayer player, int score)[] ScoreOrderedPlayers => GamePlayer.AllPlayers.Select(p => (p, GetScore(p))).OrderBy(p => -p.Item2).ToArray();

    static private string ScoreText(int score) => string.Format("{0:N0}", score) + Language.Translate("gamemode.psychoWheel.score");

    string? IGameModeModule.GetAlternativeWinOrLoseText()
    {
        var orderedPlayers = ScoreOrderedPlayers;
        if (orderedPlayers.Length <= 1) return orderedPlayers.Length == 0 ? null : ScoreText(orderedPlayers[0].score);

        int rank = 0;
        int players = 0;
        int score = -1;
        foreach (var p in orderedPlayers)
        {
            if (score != p.score)
            {
                rank = players + 1;
                score = p.score;
            }
            players++;
            if (p.player.AmOwner) break;
        }
        return Language.Translate("aeroGuesser.rank." + rank) + ": " + ScoreText(score);
    }

    string? IGameModeModule.GetAlternativePlayerStatusText()
    {
        var orderedPlayers = ScoreOrderedPlayers;
        if (orderedPlayers.Length == 0) return "";
        StringBuilder sb = new();

        int rank = 0;
        int players = 0;
        int lastScore = orderedPlayers[0].score;
        foreach (var entry in orderedPlayers)
        {
            int score = entry.score;
            if (lastScore != score) rank = players;
            lastScore = score;
            players++;

            sb.Append(Language.Translate("aeroGuesser.rank." + (rank + 1)).Color(rank < RankColor.Length ? RankColor[rank] : UnityEngine.Color.white));
            sb.Append("<indent=3.4em>");
            sb.Append(ScoreText(score));
            sb.Append("</indent>");
            sb.Append("<indent=10.4em>");
            sb.Append(entry.player.Name);
            sb.Append("</indent>");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// 通報ボタンを持たせず、緊急会議も開けないようにするアビリティです。
    /// </summary>
    private class NoMeetingAbility : AbstractPlayerAbility, IPlayerAbility
    {
        public NoMeetingAbility(GamePlayer player) : base(player) { }

        bool IPlayerAbility.HasReportButton => false;
        bool IPlayerAbility.CanReport => false;
        bool IPlayerAbility.BlockCallingEmergencyMeeting => true;
    }
}
