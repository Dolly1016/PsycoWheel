using System.Collections.Generic;
using System.Linq;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Nebula;
using Nebula.Map;
using Nebula.Modules;
using Nebula.Utilities;
using UnityEngine;
using Virial;
using Virial.Attributes;
using Virial.DI;
using Virial.Events.Game;
using Virial.Events.Game.Meeting;
using Virial.Events.Player;
using Virial.Game;
using Virial.Runtime;
using GamePlayer = Virial.Game.Player;

namespace PsycoWheel;

/// <summary>
/// キチガイ風車の出現と消滅を管理します。
/// タスクフェーズの開始・再開時にホストが出現位置を決めて全員に知らせ、会議の開始時に各自で消します。
/// </summary>
[NebulaPreprocess(PreprocessPhase.PostLoadAddons)]
public class PsychoWheelManager : AbstractModule<Virial.Game.Game>, IGameOperator
{
    static void Preprocess(NebulaPreprocessor preprocessor)
    {
        preprocessor.DIManager.RegisterModule<Virial.Game.Game>(() => new PsychoWheelManager().RegisterPermanently());
    }


    //出現位置の条件。まず全プレイヤーから遠く離れた場所を探し、無ければ条件を緩める
    private const float SpawnDistance = 8f;
    private const float RelaxedSpawnDistance = 5f;
    private const float SpawnRetryInterval = 8f; //どちらでも出現できなかったときに、遠く離れた場所を探し直す間隔
    private const float WheelSeparation = 12f; //複数出すとき、ほかの風車からこの距離以上離れた場所を優先する
    private const float MinWheelSeparation = 8f; //ほかの風車からこの距離すら離せないときは、今回は出さずに再試行を待つ
    private const float RespawnDelay = 10f; //キチガイ風車モードで、風車が消えてから次を出現させるまでの時間

    //キチガイ風車モードでは、歩き回る行き先として、近くの過去にプレイヤーがいた位置を選ぶことがある
    private const float TrailCollectInterval = 8f; //ホストがプレイヤーの位置を集める間隔
    private const float TrailLifetime = 100f; //集めてからこの時間が経った位置は捨てる
    private const float TrailMergeDistance = 0.8f; //新しく集めた位置のこの距離以内にある古い位置はすぐに捨てる
    private const float TrailMinDistance = 4f; //風車からこの距離以上離れていて
    private const float TrailMaxDistance = 12f; //この距離より近い位置を候補にする
    private const float TrailMaxChance = 0.6f; //過去の位置を選ぶ確率は、制限時間の計測を始めてから徐々に上がって、この値で止まる
    private const float TrailChanceRampTime = 120f; //最大の確率になるまでの時間

    //ホストが出現を決め、全員に知らせる。出現した風車は、はじめはホストが所有する
    static private readonly RemoteProcess<(int id, Vector2 position, GamePlayer host)> RpcSpawn = new("PsychoWheelSpawn", (message, _) =>
        NebulaAPI.CurrentGame?.GetModule<PsychoWheelManager>()?.Spawn(message.id, message.position, message.host));

    private readonly List<PsychoWheel> wheels = [];
    private int nextWheelId = 0; //ホストだけが使う
    private int pendingSpawns = 0; //ホストがまだ出現させられていない数
    private float? spawnRetryTimer = null; //ホストで再試行を待っている間だけ値を持つ
    private bool waitingForSpawnIn = false; //ホストで、スポーン位置の選択が終わるのを待っている間だけtrue
    private readonly List<float> respawnTimers = []; //ホストで、キチガイ風車モードの再出現を待っている風車ごとの残り時間
    private readonly List<(Vector2 position, float time)> playerTrails = []; //ホストが集めた、過去にプレイヤーがいた位置と集めた時刻
    private float trailCollectTimer = 0f;
    private float trailClock = 0f;
    private float? addWheelTimer = null; //ホストで、キチガイ風車モードの次に風車を増やすまでの残り時間。制限時間の計測が始まってから値を持つ
    private PsychoWheelFindButton? findButton = null;

    //ゲーム中はオプションが変わらないので、最初に風車を出すときに1回だけ取得する
    private float? playerSpeedMod = null;
    private float PlayerSpeedMod => playerSpeedMod ??= AmongUsUtil.GetCurrentNormalOption().PlayerSpeedMod;

    static public Vector2 GetRandomMapAreaPosition()
    {
        var mapArea = MapData.GetCurrentMapData().MapArea;
        return mapArea[UnityEngine.Random.Range(0, mapArea.Count)];
    }

    /// <summary>
    /// 生存している全プレイヤーから指定の距離以上離れた位置を、MapAreaからランダムに選びます。
    /// ほかの風車がいるときは、十分に離れた位置から選びます。そういった位置がなければ、ほかの風車から一番遠い位置を選びます。
    /// 一番遠い位置でもほかの風車に近すぎる場合は、見つからなかったものとします。
    /// </summary>
    private bool TryGetSpawnPosition(float distance, out Vector2 position)
    {
        var playerPositions = GamePlayer.AllPlayers.Where(p => !p.IsDead).Select(p => (Vector2)p.TruePosition).ToArray();
        var wheelPositions = wheels.Where(wheel => !wheel.IsDeadObject).Select(wheel => wheel.Position).ToArray();
        var candidates = MapData.GetCurrentMapData().MapArea
            .Select(p => (Vector2)p)
            .Where(p => playerPositions.All(playerPos => (playerPos - p).magnitude >= distance))
            .ToArray();

        position = default;
        if (candidates.Length == 0) return false;

        float DistanceToWheels(Vector2 p) => wheelPositions.Length == 0 ? float.MaxValue : wheelPositions.Min(wheelPos => (wheelPos - p).magnitude);
        var separated = candidates.Where(p => DistanceToWheels(p) >= WheelSeparation).ToArray();
        if (separated.Length > 0)
        {
            position = separated[UnityEngine.Random.Range(0, separated.Length)];
            return true;
        }

        position = candidates.MaxBy(DistanceToWheels);
        return DistanceToWheels(position) >= MinWheelSeparation;
    }

    /// <summary>
    /// ホストとして、まだ出現させられていない風車を出せるだけ出します。
    /// </summary>
    /// <param name="allowRelaxed">遠く離れた場所がないときに、条件を緩めて探す場合、true</param>
    private void SpawnPending(bool allowRelaxed)
    {
        while (pendingSpawns > 0)
        {
            if (!TryGetSpawnPosition(SpawnDistance, out var position) && !(allowRelaxed && TryGetSpawnPosition(RelaxedSpawnDistance, out position))) break;
            //Invokeは自身でも即座に実行されるため、次の位置を探すときにはこの風車も考慮される
            SpawnAsHost(position);
            pendingSpawns--;
        }
        spawnRetryTimer = pendingSpawns > 0 ? SpawnRetryInterval : null;
    }

    public PsychoWheel? GetWheel(int id) => wheels.FirstOrDefault(wheel => wheel.Id == id && !wheel.IsDeadObject);

    private void Spawn(int id, Vector2 position, GamePlayer host) => wheels.Add(new PsychoWheel(id, position, PlayerSpeedMod, host).RegisterSelf());

    private void SpawnAsHost(Vector2 position) => RpcSpawn.Invoke((nextWheelId++, position, NebulaAPI.CurrentGame!.LocalPlayer));

    //ゲーム開始時のGameStartEventはイントロの終了時に発火し、ランダムスポーンや選択式スポーンの待機はその後に続く。
    //一方、会議後のTaskPhaseRestartEventはスポーンの待機が終わってから発火する。
    //どちらの場合もイントロの表示(IsIntroDisplayed)が終わるまで待ってから出現させ、待機中に歩き回らないようにする
    void OnTaskPhaseStart(TaskPhaseStartEvent ev)
    {
        if (!AmongUsClient.Instance.AmHost) return;

        pendingSpawns = PsychoWheelOptions.NumOfWheels;
        spawnRetryTimer = null;
        waitingForSpawnIn = true;
    }

    //出現の再試行は見た目に関わらないので、ゲームの更新で数える
    void OnGameUpdate(GameUpdateEvent ev)
    {
        UpdateRespawn(ev.DeltaTime);
        UpdateAddWheel(ev.DeltaTime);
        UpdatePlayerTrails(ev.DeltaTime);

        if (waitingForSpawnIn)
        {
            if (HudManager.Instance.IsIntroDisplayed) return;
            waitingForSpawnIn = false;
            SpawnPending(true);
            return;
        }

        if (spawnRetryTimer == null) return;

        spawnRetryTimer -= ev.DeltaTime;
        if (spawnRetryTimer > 0f) return;

        SpawnPending(false);
    }

    //キチガイ風車モードでは、ホストが一定間隔で生存しているプレイヤーの位置を集めておく
    private void UpdatePlayerTrails(float deltaTime)
    {
        if (!AmongUsClient.Instance.AmHost || !(PsychoWheelGameMode.Current?.IsInProgress ?? false)) return;

        trailClock += deltaTime;
        playerTrails.RemoveAll(trail => trailClock - trail.time >= TrailLifetime);

        trailCollectTimer -= deltaTime;
        if (trailCollectTimer > 0f) return;
        trailCollectTimer += TrailCollectInterval;

        foreach (var player in GamePlayer.AllPlayers)
        {
            if (player.IsDead || player.IsDisconnected) continue;
            Vector2 pos = player.TruePosition;
            playerTrails.RemoveAll(trail => (trail.position - pos).magnitude <= TrailMergeDistance);
            playerTrails.Add((pos, trailClock));
        }
    }

    /// <summary>
    /// ホストとして、風車の近くの過去にプレイヤーがいた位置を、制限時間の経過に応じた確率で選びます。
    /// </summary>
    /// <returns>選んだ位置。選ばなかったか、候補がなければnull</returns>
    public Vector2? TryChoosePlayerTrail(Vector2 from)
    {
        float elapsed = PsychoWheelGameMode.Current?.ElapsedTime ?? 0f;
        float chance = TrailMaxChance * Mathf.Clamp01(elapsed / TrailChanceRampTime);
        if (UnityEngine.Random.value >= chance) return null;

        var candidates = playerTrails.Select(trail => trail.position).Where(pos =>
        {
            float distance = (pos - from).magnitude;
            return distance >= TrailMinDistance && distance < TrailMaxDistance;
        }).ToArray();
        if (candidates.Length == 0) return null;
        return candidates[UnityEngine.Random.Range(0, candidates.Length)];
    }

    //キチガイ風車モードでは、制限時間の間、一定間隔ごとに風車を増やす
    private void UpdateAddWheel(float deltaTime)
    {
        if (!AmongUsClient.Instance.AmHost) return;
        float interval = PsychoWheelOptions.AddIntervalOption.GetValue();
        if (interval <= 0f || !(PsychoWheelGameMode.Current?.IsInProgress ?? false)) return;

        addWheelTimer ??= interval;
        addWheelTimer -= deltaTime;
        if (addWheelTimer > 0f) return;
        addWheelTimer += interval;

        //出現位置が見つからなければ、既存の出現と同じく後で探し直す
        pendingSpawns++;
        SpawnPending(false);
    }

    //キチガイ風車モードでは、キルなどで消えた風車をしばらくしてから出し直す
    private void UpdateRespawn(float deltaTime)
    {
        if (respawnTimers.Count == 0) return;

        for (int i = 0; i < respawnTimers.Count; i++) respawnTimers[i] -= deltaTime;
        int ready = respawnTimers.RemoveAll(t => t <= 0f);
        if (ready == 0) return;

        //出現位置が見つからなければ、既存の出現と同じく後で探し直す
        pendingSpawns += ready;
        SpawnPending(false);
    }

    //キチガイ風車モードでは全員がタスクを持たない
    //タスクはゲームモードのモジュールが作られるより前(イントロの前)に配られるので、最初からあるこのモジュールで受けて、設定で判断する
    void OnSetTasks(PlayerTasksTrySetLocalEvent ev)
    {
        if (!PsychoWheelGameMode.IsSelected) return;
        ev.Tasks.Clear();

        //非ホストのゲーム開始処理(AmongUsClient.CoStartGameClient)は、自分のタスク欄(myTasks)が空の間は待ち続ける
        //クルーメイトは見出しのタスクを持たないので、代わりに見出しだけのタスクを入れておく
        //タスク欄はNoSがShipStatusの生成後に作り直すので、それを待ってから入れる
        var vanillaPlayer = ev.Player.VanillaPlayer;
        NebulaManager.Instance.StartCoroutine(CoAddHeaderTask(vanillaPlayer).WrapToIl2Cpp());
    }

    static private System.Collections.IEnumerator CoAddHeaderTask(PlayerControl player)
    {
        while (!ShipStatus.Instance) yield return null;
        yield return null;
        if (!player) yield break;
        //ImportantTextTaskは文字全体を赤のcolorタグで囲むので、内側で白に塗り直す
        PlayerTask.GetOrCreateTask<ImportantTextTask>(player).Text = "<color=#FFFFFFFF>" + Language.Translate("gamemode.psychoWheel.taskText") + "</color>";
    }

    void OnMeetingStart(MeetingStartEvent ev)
    {
        pendingSpawns = 0;
        spawnRetryTimer = null;
        waitingForSpawnIn = false;
        respawnTimers.Clear();
        foreach (var wheel in wheels) wheel.Release();
        wheels.Clear();
    }

    //GameUpdateEventはFixedUpdateから呼ばれるためキーの押下を取りこぼす。毎フレーム呼ばれるGameHudUpdateEventで受け付ける
    void OnUpdate(GameHudUpdateEvent ev)
    {
        int removed = wheels.RemoveAll(wheel => wheel.IsDeadObject);
        if (removed > 0 && AmongUsClient.Instance.AmHost && PsychoWheelGameMode.Current != null && !PsychoWheelGameMode.Current.IsTimeUp)
            for (int i = 0; i < removed; i++) respawnTimers.Add(RespawnDelay);

        //モジュールの生成時にはローカルプレイヤーがまだいないことがあるので、見つかった時点でボタンを作る
        //風車を出現させない設定なら作らない(ゲーム中に設定は変わらない)
        if (findButton == null && PsychoWheelOptions.NumOfWheels > 0 && NebulaAPI.CurrentGame?.LocalPlayer is { } localPlayerForButton) findButton = new PsychoWheelFindButton(localPlayerForButton, wheels);
    }
}
