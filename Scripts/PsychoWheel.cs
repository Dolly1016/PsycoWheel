using System.Collections.Generic;
using System.Linq;
using Nebula;
using Nebula.Behavior;
using Nebula.Game;
using Nebula.Game.Statistics;
using Nebula.Map;
using Nebula.Modules;
using Nebula.Utilities;
using UnityEngine;
using Virial;
using Virial.Events.Game;
using Virial.Events.Game.Meeting;
using Virial.Game;
using Virial.Media;
using GamePlayer = Virial.Game.Player;

namespace PsycoWheel;

/// <summary>
/// キチガイ風車1体を表します。
/// 描画用のGameObjectを所有し、移動と描画を管理します。
/// </summary>
/// <remarks>
/// 移動は所有者だけが計算し、他のクライアントへは位置の列を送ってたどらせます(FakePlayerと同様)。
/// 所有者は状態によって変わります。歩き回る間はホスト、追いかける間は認識されたプレイヤーです。
/// 所有者が変わるたびに世代(epoch)を進め、古い所有者から遅れて届いた位置は捨てます。
/// </remarks>
public class PsychoWheel : FlexibleLifespan, IGameOperator
{
    // PsychoWheel.png は128x128のコマが4x2に並んだシート
    // 0:👉 1:👆 2:👈 3:👇 4:😁 5:🫵 6:😳 7:‼️
    static internal readonly MultiImage Sprites = NebulaAPI.AddonAsset.GetResource("PsychoWheel.png")!.AsMultiImage(4, 2, 250f)!;
    private const int FaceGrinIndex = 4;
    private const int FaceRecognizedIndex = 6;
    private const int HandRecognizedIndex = 5;

    private const float WanderSpeed = 0.88f;
    //追跡中に経路を引き直す間隔(秒)。相手までの距離が遠いほど間隔を空ける
    //距離がmaxDistance以下なら、その間隔を使う。近い順に並べる
    static private readonly (float maxDistance, float interval)[] ChaseRepathIntervals = [(1f, 0.5f), (2f, 0.8f), (4f, 1.1f)];
    private const float ChaseRepathFarInterval = 2f; //どれよりも遠いとき
    private const float ChaseWindup = 1.5f; //認識してから追いかけ始めるまでの時間。この間は手を順番に突き出す

    //追いかけている風車に別のプレイヤーが近づいたら、そのプレイヤーに追いかける相手を移す
    private const float TakeOverDistance = 1.8f; //この距離より近く、影を跨がずに見えていれば移す
    private const float TakeOverWindup = 0.5f; //移した直後に立ち止まって構える時間。通常の構え(ChaseWindup)より短くする
    private const float TakeOverRewind = 5f; //移すときに、追いかけた時間をこれだけ巻き戻す
    private const float TakeOverLockTime = 3f; //移してからしばらくは、続けて移さない(すぐに元の相手へ戻るのを防ぐ)

    //追いかけられている本人以外にも、警報の音だけは小さく聞かせる
    private const float OthersAlarmMinVolume = 0.4f; //遠いとき
    private const float OthersAlarmMaxVolume = 0.7f; //すぐ近くのとき
    private const float OthersAlarmNearDistance = 5f; //この距離より近づくと、近いほど大きくする
    private const float ChaseSpeedUpTime = 10f; //追いかけ始めてからこの時間が経つとスピードアップする
    private const float ChaseIgnoreWallsTime = 20f; //追いかけ始めてからこの時間が経つと、経路探索をやめて壁を無視してまっすぐ向かう
    //追いかける速さ。[通常, スピードアップした後, 壁を無視して向かうとき]の順に並べる
    static private readonly float[] ChaseSpeeds = [2.12f, 2.4f, 2.75f];

    //キチガイ風車モードでの追いかけ方。速さはChaseSpeedsとは別に、同じ並びで決める
    static private readonly float[] ModeChaseSpeeds = [2.4f, 2.51f, 2.8f];
    //風車ごとに何度目のチェイスかで、追いかけ続ける時間を決める(構えの後から数える)。配列より多い回数のチェイスは時間制限なし
    static private readonly float[] ModeChaseDurations = [10f, 20f];
    private const float GiveUpRecognizeCooldown = 3f; //追いかけるのをやめてから、また認識し始めるまでの時間
    //警報の音程をランダムに変える範囲。スピードアップした段階と、壁を無視する段階で別々に決める
    static private readonly (float min, float max) SpedUpAlarmPitchRange = (0.8f, 1.2f);
    static private readonly (float min, float max) WallIgnoringAlarmPitchRange = (0.6f, 1.4f);
    private const float CatchingAlarmIntervalRate = 0.8f; //キル演出中は警報の音をさらに短い間隔で鳴らす

    //認識直後の構え。手を1つずつ突き出して戻す。前の手が戻りきる前に次の手が突き出し始める
    private const float WindupPulseInterval = 0.25f; //次の手が突き出し始めるまでの間隔
    private const float WindupPulseDuration = 0.5f; //手1つ分の突き出して戻るまでの時間

    //歩き回るときのルール(ホストが判断する)
    //行き先に着かなくても、この範囲のランダムな秒数で立ち止まる。ゲームモードごとに決める
    static private readonly (float min, float max) WanderDurationRange = (8f, 30f);
    static private readonly (float min, float max) ModeWanderDurationRange = (5f, 15f);

    //キチガイ風車モードでの、立ち止まったときの行動の選ばれやすさ(重み)。通常のゲームでは常に周りを見渡す
    static private readonly (WanderPattern pattern, float weight)[] ModeWanderPatternWeights = [(WanderPattern.Walk, 1f), (WanderPattern.Rush, 1f), (WanderPattern.Erratic, 1f)];
    private const float RushSpeed = 1.8f; //周りを見渡す代わりに、普段より速く1か所を目指す
    static private readonly (float min, float max) RushDurationRange = (4f, 8f);
    private const float ErraticSpeed = 1.8f; //普段より速く、行き先を変えながらうろつく
    static private readonly (float min, float max) ErraticDurationRange = (8f, 12f);
    static private readonly (float min, float max) ErraticRetargetIntervalRange = (1f, 2f); //行き先を変える間隔
    private const float LookAroundDelay = 0.2f; //立ち止まってから周りを見渡し始めるまでの時間
    private const float LookAroundDuration = 0.8f; //探知範囲の円を出して周りを見渡している時間
    private const float LookAroundCheckDelay = 0.5f; //円を出してから、探知範囲のプレイヤーを認識し始めるまでの時間
    static private readonly Virial.Color LookAroundCircleColor = new(250f / 255f, 167f / 255f, 0f);
    private const float LookAroundDistance = 4.9f; //見渡したときに、影を跨がずに見えたプレイヤーを認識する距離
    private const float ImmediateRecognizeDistance = 1.8f; //見渡すかどうかに関係なく、即座に認識する距離

    //動こうとしているのに動けていないときの脱出(所有者が判断する)
    private const float StuckTime = 1f; //この時間、ほとんど位置が変わらなければ動けていないとみなす
    private const float StuckDistance = 0.1f; //この距離以上動けば、動けているとみなす
    private const float EscapeMinNodeDistance = 0.5f; //すでにいるノードには逃げないよう、この距離より近いノードは選ばない

    //位置の同期
    private const int SyncInterval = 2; //所有者が位置を送る間隔(ゲームの更新の回数)
    private const int MaxIncomingPositions = 8; //受け取った位置が溜まりすぎたら古いものを捨てて追いつく
    private const float WheelRotateSpeed = -180f; //負で時計回り
    private const float WheelFastRotateSpeed = WheelRotateSpeed * 5f;
    private const float HandDistance = 0.55f;
    private const float BodyHeight = 0.45f; //足元(経路上の点)から体の中心までの高さ
    private const float LightStrength = 1f; //Phosphorusのランプ強度の既定値と同じ
    private const float LightZ = -10f; //GenerateCustomLightと同じ奥行き
    private const float BodyZ = LightZ - 0.01f; //ライトより少し手前に置き、ライトの具合によらず見えるようにする
    static private readonly UnityEngine.Color WanderLightColor = new(1f, 1f, 1f, 0.8f);
    static private readonly UnityEngine.Color ChaseLightColor = new(1f, 0f, 0f, 1f);

    //追跡中の回転のリズム。通常の速さから始めて、(秒数, 速く回すか)を順に繰り返す
    static private readonly (float duration, bool fast)[] ChaseRotateRhythm = [(0.4f, false), (0.3f, true), (0.7f, false), (0.3f, true), (0.2f, false), (0.2f, true)];
    static private readonly float ChaseRotateRhythmLength = ChaseRotateRhythm.Sum(r => r.duration);

    //追跡中の手の突き刺し
    private const float ThrustInterval = 0.35f;
    private const float ThrustScale = 0.35f; //最大でこの割合だけ手を拡大する
    private const float ThrustDistance = 0.2f; //最大でこの割合だけ手を外へ突き出す

    //捕獲(キル)演出
    private const float CatchDistance = 0.5f;
    private const float CinematicView = 0.55f; //バブルガンと同じカメラの寄り具合
    private const float CinematicDuration = 2.2f; //カメラが注目し、風車がオーバーレイより前面に出ている時間
    private const float CinematicZ = -100f; //オーバーレイより前面に出すときの体の奥行き(PlayCinematicKillと同じ)
    //被害者視点で迫ってくるときの拡大。じわじわ大きくなりながら、3回ダン、ダン、ダン！と手前に飛んでくる
    static private readonly float[] ApproachStepTimes = [0.4f, 0.9f, 1.4f];
    private const float HudFaceTime = 1.8f; //最後の段階。顔をHUDに出し、画面の手より手前でぐっと拡大させる
    private const float ApproachStepFactor = 1.4f; //1段階で大きくなる倍率
    private const float ApproachStepDuration = 0.1f; //1段階の飛び出しにかける時間
    private const float ApproachCreep = 0.1f; //1秒あたりにじわじわ大きくなる割合
    private const float ShrinkDuration = 0.3f; //消滅時の縮小アニメーション1つ分の時間

    static private readonly Virial.Text.CommunicableTextTag CaughtState = PsychoWheelTags.Caught;

    //歩き回るときの移動の仕方
    private enum WanderPattern
    {
        Walk, //周りを見渡してから、普段の速さで歩く
        Rush, //見渡さずに、普段より速く1か所を目指す
        Erratic, //見渡さずに、普段より速く行き先を変えながらうろつく
    }

    private enum State
    {
        Wandering,
        Chasing,
        Catching, //自分が捕まった。被害者視点の演出中
        Vanishing, //手、全体の順に縮小して消える
    }

    private readonly GameObject root;
    private readonly Transform bodyTransform;
    private readonly Transform wheelTransform; //手4つの親。手をまとめて顔の中心に向かって縮小させるために使う
    private readonly SpriteRenderer face;

    //風車を直接クリックして見る。顔と手を覆う範囲を当たり判定にする
    private const float ClickRadius = 0.8f;
    /// <summary>
    /// 風車がクリックされたときに呼ばれます。見るボタンが設定します。
    /// </summary>
    static public System.Action<PsychoWheel>? ClickHandler { get; set; } = null;
    private readonly SpriteRenderer[] hands = new SpriteRenderer[4];
    private readonly SpriteRenderer light;
    private readonly Vector3 lightScale;
    private CatchScreenHands? screenHands = null; //被害者視点でのみ画面の周囲に出る手
    private ChaseAlarm? alarm = null; //追いかけられている本人の画面でのみ出す警報
    private bool meetingCalled = false; //会議が呼ばれたら、その場で止まって捕まえなくなる

    private State state = State.Wandering;
    private float stateTime = 0f;
    private float currentWindup = ChaseWindup; //今のチェイスの構えの時間
    private float chaseTimeOffset = 0f; //引き継いだチェイスで、それまでに追いかけていた時間
    private GamePlayer? chaseTarget = null;
    private float repathTimer = 0f;

    //歩き回る間の状態(所有者であるホストだけが使う)
    private bool wanderMoving = false;
    private float wanderTimer = 0f; //立ち止まるまでの残り時間
    private float stopTime = 0f; //立ち止まってからの時間
    private bool lookStarted = false; //今回立ち止まってから見渡し始めたか
    private WanderPattern wanderPattern = WanderPattern.Walk; //全クライアントで共有する。所有者以外は位置をたどる速さに使う
    private float erraticRetargetTimer = 0f;

    //動けていないときの脱出(所有者だけが使う)
    private float stuckTime = 0f;
    private Vector2 stuckAnchor; //最後に動けていると判断した位置
    private Vector2? escapeTarget = null; //壁を無視して向かっている近くのノード

    //見渡している間に出す探知範囲の円(全クライアント)
    private EffectCircle? lookCircle = null;
    private float lookCircleTime = 0f;

    //認識の判定は各クライアントが自分について行い、ホストに要求する
    private const float RecognizeRequestInterval = 0.5f; //要求の返事を待つ間に送り直さない時間
    private float recognizeRequestCooldown = 0f;

    //同期
    public int Id { get; }
    private GamePlayer owner;
    private bool AmOwner => owner.AmOwner;
    private int epoch = 0; //所有者が変わるたびに進める世代
    private int lastSequence = 0; //所有者は最後に送った位置の番号、それ以外は最後に受け取った位置の番号
    private readonly List<Vector2> sendQueue = [];
    private readonly Queue<Vector2> incomingQueue = new();
    private int syncTimer = SyncInterval;
    private Vector2 lastQueuedPosition;

    private readonly float speedMod;
    private readonly bool isGameMode; //キチガイ風車モードのゲームで出現した場合、true
    private int chaseCount = 0; //この風車がこれまでに始めたチェイスの回数(今のチェイスを含む)
    private Vector2 position;
    public Vector2 Position => position;
    public bool IsWandering => state == State.Wandering;
    /// <summary>
    /// 追いかけている相手。追いかけていなければnullです。
    /// </summary>
    public GamePlayer? ChaseTarget => state == State.Chasing ? chaseTarget : null;
    private const float PathRadius = 0.05f; //経路を探すときに、壁を避けて通れるか調べる太さ(NoSの既定は0.15)
    private Vector2[]? path = null;
    private int nextPathIndex = 0;
    private float time = 0f;
    private float wheelAngle = 0f;

    /// <param name="id">全クライアントで共通の識別子</param>
    /// <param name="speedMod">移動速度に掛ける倍率(プレイヤー速度のオプション値)</param>
    /// <param name="owner">最初の所有者(ホスト)</param>
    public PsychoWheel(int id, Vector2 position, float speedMod, GamePlayer owner) : base(NebulaAPI.CurrentGame!)
    {
        Id = id;
        this.position = position;
        this.speedMod = speedMod;
        isGameMode = PsychoWheelGameMode.Current != null;
        this.owner = owner;
        lastQueuedPosition = position;

        root = new GameObject("PsychoWheel");
        bodyTransform = new GameObject("Body").transform;
        bodyTransform.SetParent(root.transform, false);
        wheelTransform = new GameObject("Wheel").transform;
        wheelTransform.SetParent(bodyTransform, false);

        face = CreateRenderer("Face", bodyTransform, FaceGrinIndex, Vector2.zero, -0.01f);

        //体と一緒に動く当たり判定。壁や影の判定に混ざらないよう、トリガーにする
        var clickCollider = UnityHelper.CreateObject<CircleCollider2D>("ClickArea", bodyTransform, Vector3.zero);
        clickCollider.radius = ClickRadius;
        clickCollider.isTrigger = true;
        var clickButton = clickCollider.gameObject.SetUpButton(false);
        clickButton.OnClick.AddListener(() => ClickHandler?.Invoke(this));
        //画像は UpdateHands で手の向きに応じて差し替える
        for (int i = 0; i < hands.Length; i++) hands[i] = CreateRenderer("Hand" + i, wheelTransform, i, Vector2.zero, 0f);

        //Phosphorusのランタンと同じ円形の光源。rootの子にして一緒に移動・破棄させる
        light = AmongUsUtil.GenerateCustomLight(position);
        light.transform.SetParent(root.transform, false);
        light.transform.localPosition = new Vector3(0f, BodyHeight, LightZ);
        light.transform.localScale *= LightStrength;
        lightScale = light.transform.localScale;

        ChangeState(State.Wandering, null);
        UpdateTransform(0f);
    }

    private SpriteRenderer CreateRenderer(string name, Transform parent, int spriteIndex, Vector2 localPos, float z)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(parent, false);
        obj.transform.localPosition = new Vector3(localPos.x, localPos.y, z);
        var renderer = obj.AddComponent<SpriteRenderer>();
        renderer.sprite = Sprites.GetSprite(spriteIndex);
        return renderer;
    }

    /// <summary>
    /// 他人目線の捕獲演出(その場で縮小して消える)を再生します。
    /// </summary>
    private void Vanish()
    {
        if (state is State.Catching or State.Vanishing) return;
        ChangeState(State.Vanishing, null);
    }

    private void ChangeState(State state, GamePlayer? target)
    {
        this.state = state;
        stateTime = 0f;
        chaseTarget = target;
        bool recognized = state != State.Wandering;
        light.material.color = recognized ? ChaseLightColor : WanderLightColor;
        face.sprite = Sprites.GetSprite(recognized ? FaceRecognizedIndex : FaceGrinIndex);
        face.enabled = true;

        //追いかけられている間だけ、本人の画面で警報を出す。捕まったときはキル演出が終わるまで鳴らし続ける
        if (state != State.Catching)
        {
            alarm?.Stop();
            if (state == State.Chasing && !meetingCalled)
                alarm = (target?.AmOwner ?? false) ? new ChaseAlarm(true, null) : new ChaseAlarm(false, GetOthersAlarmVolume);
            else
                alarm = null;
        }
        else if (alarm != null)
        {
            alarm.SoundIntervalRate = CatchingAlarmIntervalRate;
        }

        //引き継ぐとき以外は、通常の構えから数え始める
        currentWindup = ChaseWindup;
        chaseTimeOffset = 0f;

        //状態が変わったら今の経路は捨てて、次の更新で引き直す
        path = null;
        repathTimer = 0f;
        ResetStuck();
        escapeTarget = null;

        //歩き回るときは、立ち止まった状態から始めて周りを見渡す
        wanderMoving = false;
        stopTime = 0f;
        lookStarted = false;
        wanderPattern = WanderPattern.Walk;
    }

    //ホストが歩き回るときの移動の仕方を変えた。所有者以外が位置をたどる速さを合わせる
    static private readonly RemoteProcess<(int id, int epoch, int pattern)> RpcWanderPattern = new("PsychoWheelWanderPattern", (message, _) =>
    {
        var wheel = GetWheel(message.id);
        if (wheel == null || message.epoch != wheel.epoch || wheel.state != State.Wandering) return;
        wheel.wanderPattern = (WanderPattern)message.pattern;
    });

    private float CurrentWanderSpeed => wanderPattern switch
    {
        WanderPattern.Rush => RushSpeed,
        WanderPattern.Erratic => ErraticSpeed,
        _ => WanderSpeed,
    };

    /// <summary>
    /// 所有者を変え、位置を合わせ直します。
    /// </summary>
    private void TakeOver(int epoch, GamePlayer owner, Vector2 position)
    {
        this.epoch = epoch;
        this.owner = owner;
        this.position = position;
        lastQueuedPosition = position;
        lastSequence = 0;
        sendQueue.Clear();
        incomingQueue.Clear();
        syncTimer = SyncInterval;
    }

    static private GamePlayer? Host => GamePlayer.AllPlayers.FirstOrDefault(p => p.AmHost);

    static private PsychoWheel? GetWheel(int id) => NebulaAPI.CurrentGame?.GetModule<PsychoWheelManager>()?.GetWheel(id);

    //歩き回っている(所有者はホスト)風車がプレイヤーを認識した。以降はそのプレイヤーが所有者になる
    static private readonly RemoteProcess<(int id, int epoch, GamePlayer player, Vector2 position)> RpcRecognize = new("PsychoWheelRecognize", (message, _) =>
    {
        var wheel = GetWheel(message.id);
        if (wheel == null || message.epoch <= wheel.epoch) return;
        wheel.TakeOver(message.epoch, message.player, message.position);
        wheel.chaseCount++;
        wheel.ChangeState(State.Chasing, message.player);
    });

    //追いかけている風車に近づいたプレイヤーへ、追いかける相手と移動の計算を移す
    static private readonly RemoteProcess<(int id, int epoch, GamePlayer player, Vector2 position, float chaseTime)> RpcTakeOverChase = new("PsychoWheelTakeOverChase", (message, _) =>
    {
        var wheel = GetWheel(message.id);
        if (wheel == null || message.epoch <= wheel.epoch) return;
        wheel.TakeOver(message.epoch, message.player, message.position);
        //同じチェイスの続きなので、チェイスの回数は数えない
        wheel.ChangeState(State.Chasing, message.player);
        wheel.currentWindup = TakeOverWindup;
        wheel.chaseTimeOffset = Mathf.Max(0f, message.chaseTime - TakeOverRewind);
    });

    //あるクライアントが、追いかけている風車に自分が近づいたと判定した。ホストが先着順に受け付ける
    static private readonly RemoteProcess<(int id, int epoch, GamePlayer player)> RpcRequestTakeOverChase = new("PsychoWheelRequestTakeOverChase", (message, _) =>
    {
        if (!AmongUsClient.Instance.AmHost) return;
        var wheel = GetWheel(message.id);
        if (wheel == null || wheel.state != State.Chasing || wheel.meetingCalled || message.epoch != wheel.epoch) return;
        if (!wheel.CanTakeOverChase || message.player == wheel.chaseTarget || message.player.IsDead || message.player.IsDisconnected) return;
        RpcTakeOverChase.Invoke((wheel.Id, wheel.epoch + 1, message.player, wheel.position, wheel.ChaseTime));
    });

    //あるクライアントが、自分は風車に認識されたと判定した。ホストが先着順に受け付ける
    //受け付けるとRpcRecognizeで世代が進むため、同じ世代に向けた後続の要求は弾かれる
    static private readonly RemoteProcess<(int id, int epoch, GamePlayer player)> RpcRequestRecognize = new("PsychoWheelRequestRecognize", (message, _) =>
    {
        if (!AmongUsClient.Instance.AmHost) return;
        var wheel = GetWheel(message.id);
        if (wheel == null || wheel.state != State.Wandering || wheel.meetingCalled || message.epoch != wheel.epoch) return;
        if (message.player.IsDead || message.player.IsDisconnected) return;
        RpcRecognize.Invoke((wheel.Id, wheel.epoch + 1, message.player, wheel.position));
    });

    //追いかけていた相手がいなくなったので、ホストが管理を引き取って歩き回る状態に戻す
    static private readonly RemoteProcess<(int id, int epoch, Vector2 position)> RpcReturnToHost = new("PsychoWheelReturnToHost", (message, _) =>
    {
        var wheel = GetWheel(message.id);
        var host = Host;
        if (wheel == null || host == null || message.epoch <= wheel.epoch) return;
        wheel.TakeOver(message.epoch, host, message.position);
        wheel.ChangeState(State.Wandering, null);
        //すぐそばにいる相手をそのまま認識し直さないよう、少し待つ
        wheel.recognizeRequestCooldown = GiveUpRecognizeCooldown;
    });

    //追いかけていた相手を捕まえた。被害者は捕獲演出を、それ以外はその場で消える演出を再生する
    static private readonly RemoteProcess<(int id, int epoch, GamePlayer victim)> RpcCatch = new("PsychoWheelCatch", (message, _) =>
    {
        var wheel = GetWheel(message.id);
        if (wheel == null || wheel.meetingCalled || message.epoch <= wheel.epoch) return;
        wheel.epoch = message.epoch;
        if (message.victim.AmOwner) wheel.PlayCatchAnimation(message.victim);
        else wheel.Vanish();
    });

    //ホストが周りを見渡し始めた。全員に探知範囲の円を見せる
    static private readonly RemoteProcess<(int id, int epoch)> RpcLookAround = new("PsychoWheelLookAround", (message, _) =>
    {
        var wheel = GetWheel(message.id);
        if (wheel == null || message.epoch != wheel.epoch) return;
        wheel.ShowLookCircle();
    });

    private void ShowLookCircle()
    {
        if (lookCircle) lookCircle!.DestroyFast();
        //足元を中心に、探知範囲と同じ半径の円を広げる。rootの子にして一緒に破棄させる
        lookCircle = EffectCircle.SpawnEffectCircle(root.transform, Virial.Compat.Vector3.Zero, LookAroundCircleColor, LookAroundDistance, null, true);
        lookCircleTime = 0f;
    }

    private void UpdateLookCircle(float deltaTime)
    {
        if (lookCircle == null) return;
        lookCircleTime += deltaTime;
        if (lookCircleTime < LookAroundDuration && state == State.Wandering) return;
        if (lookCircle) lookCircle.Disappear();
        lookCircle = null;
    }

    //所有者からの位置の列。頻繁に送るので信頼性は求めず、順序の入れ替わりは番号で弾く
    static private readonly RemoteProcess<(int id, int epoch, int sequence, Vector2[] positions)> RpcSync = new("PsychoWheelSync", (message, calledByMe) =>
    {
        if (calledByMe) return;
        var wheel = GetWheel(message.id);
        if (wheel == null || wheel.AmOwner || message.epoch != wheel.epoch) return;
        for (int i = 0; i < message.positions.Length; i++)
        {
            int sequence = message.sequence + i;
            if (sequence <= wheel.lastSequence) continue;
            wheel.lastSequence = sequence;
            wheel.incomingQueue.Enqueue(message.positions[i]);
        }
    }, false);

    //通報・緊急会議の時点で追跡を終える。風車自体は会議の開始時に消える
    void OnMeetingPreStart(MeetingPreStartEvent ev)
    {
        meetingCalled = true;
        alarm?.Stop();
        alarm = null;
    }

    void OnUpdate(GameUpdateEvent ev)
    {
        if (!root) return;

        time += ev.DeltaTime;
        stateTime += ev.DeltaTime;
        UpdateLookCircle(ev.DeltaTime);
        UpdateRecognitionRequest(ev.DeltaTime);
        UpdateMoving(ev.DeltaTime);
        if (IsDeadObject) return;
        //スピードアップしたら警報の音程を乱す。捕まった後も、そのときの状態のまま鳴らし続ける
        if (alarm != null && state == State.Chasing) alarm.PitchRange = IsIgnoringWalls ? WallIgnoringAlarmPitchRange : IsSpedUp ? SpedUpAlarmPitchRange : null;
        UpdateTransform(ev.DeltaTime);
    }

    private void UpdateMoving(float deltaTime)
    {
        switch (state)
        {
            case State.Catching:
                screenHands?.Update(deltaTime, GetApproachLevel(stateTime));
                if (stateTime >= HudFaceTime && face.enabled) ShowHudFace();
                if (stateTime > CinematicDuration)
                {
                    //注目が解除されるので、画面の周囲の手もその場で縮小させる
                    screenHands?.Shrink();
                    ChangeState(State.Vanishing, null);
                }
                return;
            case State.Vanishing:
                screenHands?.Update(deltaTime, 0);
                if (stateTime > ShrinkDuration * 2f) Release();
                return;
            case State.Wandering or State.Chasing when meetingCalled:
                return;
            case State.Wandering:
                if (AmOwner) UpdateMovingAsOwner(deltaTime, wanderMoving || stopTime >= LookAroundDelay + LookAroundDuration, UpdateWanderingAsOwner);
                break;
            case State.Chasing:
                //追いかけていた相手が死亡・切断したら、ホストが管理を引き取る
                if (AmongUsClient.Instance.AmHost && (chaseTarget == null || chaseTarget.IsDead || chaseTarget.IsDisconnected))
                {
                    RpcReturnToHost.Invoke((Id, epoch + 1, position));
                    return;
                }
                if (AmOwner) UpdateMovingAsOwner(deltaTime, chaseTarget != null && !chaseTarget.IsDead && !IsWindingUp, UpdateChasingAsOwner);
                break;
        }

        if (state is not (State.Wandering or State.Chasing)) return;
        if (AmOwner) SendPosition();
        else FollowIncomingPositions(deltaTime);
    }

    /// <summary>
    /// 所有者として動かし、動こうとしているのに動けていなければ、近くのノードへ壁を無視して向かわせます。
    /// </summary>
    /// <param name="wantsToMove">この更新で動こうとしている場合、true。立ち止まって見渡している間や構えている間はfalse</param>
    /// <param name="update">通常の移動</param>
    private void UpdateMovingAsOwner(float deltaTime, bool wantsToMove, System.Action<float> update)
    {
        if (escapeTarget != null)
        {
            //脱出中も、追いかけている相手に追いついたら捕まえる
            if (state == State.Chasing && chaseTarget != null && TryCatch(chaseTarget)) return;
            UpdateEscape(deltaTime);
            return;
        }

        update(deltaTime);

        //経路が無い場合に限らず、引っかかるなどして位置が変わらない場合も含めて判定する
        if (!wantsToMove || (position - stuckAnchor).magnitude >= StuckDistance)
        {
            ResetStuck();
            return;
        }
        stuckTime += deltaTime;
        if (stuckTime < StuckTime) return;

        ResetStuck();
        escapeTarget = FindEscapeNode();
    }

    private void ResetStuck()
    {
        stuckTime = 0f;
        stuckAnchor = position;
    }

    private void UpdateEscape(float deltaTime)
    {
        float speed = state == State.Chasing ? CurrentChaseSpeed : CurrentWanderSpeed;
        position = Vector2.MoveTowards(position, escapeTarget!.Value, speed * speedMod * deltaTime);
        if (position != escapeTarget.Value) return;

        //着いたら、次の更新で経路を引き直す
        escapeTarget = null;
        path = null;
        repathTimer = 0f;
        ResetStuck();
    }

    /// <summary>
    /// 今いる位置から一番近いナビノードを探します。すでにいるノードは除きます。
    /// </summary>
    private Vector2? FindEscapeNode()
    {
        Vector2? nearest = null;
        float nearestDistance = float.MaxValue;
        foreach (var node in MapData.GetCurrentMapData().MapNavData.MainNodes)
        {
            var nodePos = new Vector2(node.X, node.Y);
            float distance = (nodePos - position).magnitude;
            if (distance < EscapeMinNodeDistance || distance >= nearestDistance) continue;
            nearest = nodePos;
            nearestDistance = distance;
        }
        return nearest;
    }

    /// <summary>
    /// 歩き回る風車の所有者(ホスト)として、行き先を決めて歩かせ、立ち止まったら周りを見渡します。
    /// キチガイ風車モードでは、周りを見渡す代わりに速く移動することもあります。
    /// </summary>
    private void UpdateWanderingAsOwner(float deltaTime)
    {
        if (wanderMoving)
        {
            wanderTimer -= deltaTime;
            bool arrived = MoveAlongPath(CurrentWanderSpeed * speedMod * deltaTime);

            //うろつく間は、一定間隔か行き先に着いたときに次の行き先へ切り替える
            if (wanderPattern == WanderPattern.Erratic && wanderTimer > 0f)
            {
                erraticRetargetTimer -= deltaTime;
                if ((arrived || erraticRetargetTimer <= 0f) && TrySetPath(ChooseWanderDestination()))
                {
                    erraticRetargetTimer = RandomIn(ErraticRetargetIntervalRange);
                    arrived = false;
                }
            }

            if (arrived || wanderTimer <= 0f)
            {
                wanderMoving = false;
                stopTime = 0f;
                lookStarted = false;
                path = null;
                StartNextWanderPattern();
            }
            return;
        }

        //少し立ち止まってから、探知範囲の円を出して見渡す
        stopTime += deltaTime;
        if (stopTime < LookAroundDelay) return;
        if (!lookStarted)
        {
            lookStarted = true;
            RpcLookAround.Invoke((Id, epoch));
        }

        //見渡している間は立ち止まったまま、各クライアントからの認識の要求を待つ(UpdateRecognitionRequest)
        if (stopTime < LookAroundDelay + LookAroundDuration) return;

        //誰からも要求が来なければ次の行き先を決める。経路が見つからなければ次の更新で再挑戦する
        TryStartWanderMove(WanderPattern.Walk);
    }

    /// <summary>
    /// 歩き回るときの次の行き先を選びます。キチガイ風車モードでは、近くの過去にプレイヤーがいた位置を選ぶこともあります。
    /// </summary>
    private Vector2 ChooseWanderDestination()
    {
        if (isGameMode && NebulaAPI.CurrentGame?.GetModule<PsychoWheelManager>()?.TryChoosePlayerTrail(position) is { } trail) return trail;
        return PsychoWheelManager.GetRandomMapAreaPosition();
    }

    static private float RandomIn((float min, float max) range) => UnityEngine.Random.Range(range.min, range.max);

    /// <summary>
    /// 立ち止まったときに、次の行動を選びます。周りを見渡す場合は、立ち止まったまま見渡す流れに任せます。
    /// </summary>
    private void StartNextWanderPattern()
    {
        if (!isGameMode) return;

        float sum = ModeWanderPatternWeights.Sum(e => e.weight);
        float value = UnityEngine.Random.Range(0f, sum);
        var pattern = WanderPattern.Walk;
        foreach (var (candidate, weight) in ModeWanderPatternWeights)
        {
            pattern = candidate;
            value -= weight;
            if (value < 0f) break;
        }

        //速く移動する行動は見渡さずにすぐ始める。経路が見つからなければ、いつも通り見渡す
        if (pattern != WanderPattern.Walk) TryStartWanderMove(pattern);
    }

    /// <summary>
    /// 行き先を決めて、指定の仕方で移動し始めます。
    /// </summary>
    /// <returns>経路が見つかって移動し始めた場合、true</returns>
    private bool TryStartWanderMove(WanderPattern pattern)
    {
        if (!TrySetPath(ChooseWanderDestination())) return false;

        wanderMoving = true;
        wanderTimer = RandomIn(pattern switch
        {
            WanderPattern.Rush => RushDurationRange,
            WanderPattern.Erratic => ErraticDurationRange,
            _ => isGameMode ? ModeWanderDurationRange : WanderDurationRange,
        });
        erraticRetargetTimer = RandomIn(ErraticRetargetIntervalRange);
        if (wanderPattern != pattern) RpcWanderPattern.Invoke((Id, epoch, (int)pattern));
        return true;
    }

    /// <summary>
    /// 自分のプレイヤーが歩き回る風車に認識されたかを自分の画面上の位置で判定し、されていればホストに要求します。
    /// すぐ近くまで来たら常に、見渡している間(円が広がってから消えるまで)は探知範囲まで、それぞれ影を跨がずに見えていれば認識されます。
    /// </summary>
    private void UpdateRecognitionRequest(float deltaTime)
    {
        recognizeRequestCooldown -= deltaTime;
        if (meetingCalled || recognizeRequestCooldown > 0f) return;
        if (state == State.Chasing)
        {
            UpdateTakeOverRequest();
            return;
        }
        if (state != State.Wandering) return;

        var localPlayer = NebulaAPI.CurrentGame?.LocalPlayer;
        if (localPlayer == null || localPlayer.IsDead || !localPlayer.IsActive) return;

        Vector2 playerPos = localPlayer.TruePosition;
        float distance = (playerPos - position).magnitude;
        //すぐ近くなら常に、見渡している間は探知範囲まで、影を跨がずに見えていれば認識される
        bool inRange = distance <= ImmediateRecognizeDistance || (IsLookingAround && distance <= LookAroundDistance);
        if (!inRange) return;
        var map = NebulaAPI.CurrentGame?.CurrentMap;
        if (map == null || map.AnyShadowsBetween(position, playerPos)) return;

        recognizeRequestCooldown = RecognizeRequestInterval;
        RpcRequestRecognize.Invoke((Id, epoch, localPlayer));
    }

    private bool IsLookingAround => lookCircle != null && lookCircleTime >= LookAroundCheckDelay;

    /// <summary>
    /// 追いかけられていない自分のプレイヤーが、追いかけている風車のすぐ近くにいれば、追いかける相手を自分に移すようホストに要求します。
    /// </summary>
    private void UpdateTakeOverRequest()
    {
        if (!CanTakeOverChase) return;
        var localPlayer = NebulaAPI.CurrentGame?.LocalPlayer;
        if (localPlayer == null || localPlayer == chaseTarget || localPlayer.IsDead || !localPlayer.IsActive) return;

        Vector2 playerPos = localPlayer.TruePosition;
        if ((playerPos - position).magnitude > TakeOverDistance) return;
        var map = NebulaAPI.CurrentGame?.CurrentMap;
        if (map == null || map.AnyShadowsBetween(position, playerPos)) return;

        recognizeRequestCooldown = RecognizeRequestInterval;
        RpcRequestTakeOverChase.Invoke((Id, epoch, localPlayer));
    }

    //構えている間と、移した直後は移さない
    private bool CanTakeOverChase => state == State.Chasing && !IsWindingUp && stateTime >= currentWindup + TakeOverLockTime;

    /// <summary>
    /// 追いかけられている本人以外が聞く警報の音量の倍率です。近いほど大きくなります。
    /// </summary>
    private float GetOthersAlarmVolume()
    {
        var localPlayer = NebulaAPI.CurrentGame?.LocalPlayer;
        if (localPlayer == null) return OthersAlarmMinVolume;
        float distance = ((Vector2)localPlayer.TruePosition - position).magnitude;
        return Mathf.Lerp(OthersAlarmMaxVolume, OthersAlarmMinVolume, distance / OthersAlarmNearDistance);
    }

    /// <summary>
    /// 追いかける風車の所有者(認識されたプレイヤー)として、自分自身を追いかけさせます。
    /// </summary>
    private void UpdateChasingAsOwner(float deltaTime)
    {
        if (chaseTarget == null || chaseTarget.IsDead) return;
        //構えている間はその場に留まり、捕まえもしない
        if (IsWindingUp) return;
        if (TryCatch(chaseTarget)) return;

        //追いかける時間が過ぎたら諦めて、ホストに戻して歩き回らせる
        if (ChaseDurationLimit is { } limit && ChaseTime >= limit)
        {
            RpcReturnToHost.Invoke((Id, epoch + 1, position));
            return;
        }

        //しばらく追いかけ続けたら、壁を無視して相手にまっすぐ向かう
        if (IsIgnoringWalls)
        {
            path = null;
            position = Vector2.MoveTowards(position, chaseTarget.TruePosition, CurrentChaseSpeed * speedMod * deltaTime);
            return;
        }

        //相手は動き続けるので、一定間隔で経路を引き直す
        repathTimer -= deltaTime;
        if (repathTimer <= 0f || path == null || nextPathIndex >= path.Length)
        {
            repathTimer = GetChaseRepathInterval(((Vector2)chaseTarget.TruePosition - position).magnitude);
            if (!TrySetPath(chaseTarget.TruePosition)) return;
        }
        MoveAlongPath(CurrentChaseSpeed * speedMod * deltaTime);
    }

    static private float GetChaseRepathInterval(float distance)
    {
        foreach (var (maxDistance, interval) in ChaseRepathIntervals) if (distance <= maxDistance) return interval;
        return ChaseRepathFarInterval;
    }

    /// <summary>
    /// 経路に沿って進めます。
    /// </summary>
    /// <returns>経路の終点に着いた場合、true</returns>
    private bool MoveAlongPath(float moveDistance)
    {
        if (path == null) return true;
        while (moveDistance > 0f && nextPathIndex < path.Length)
        {
            var target = path[nextPathIndex];
            var diff = target - position;
            float distance = diff.magnitude;
            if (distance <= moveDistance)
            {
                position = target;
                moveDistance -= distance;
                nextPathIndex++;
            }
            else
            {
                position += diff / distance * moveDistance;
                moveDistance = 0f;
            }
        }
        return nextPathIndex >= path.Length;
    }

    /// <summary>
    /// 所有者として、動いた位置を溜めて一定間隔で送ります。
    /// </summary>
    private void SendPosition()
    {
        if ((position - lastQueuedPosition).sqrMagnitude > 0.0001f * 0.0001f)
        {
            sendQueue.Add(position);
            lastQueuedPosition = position;
        }

        syncTimer--;
        if (syncTimer > 0 || sendQueue.Count == 0) return;
        syncTimer = SyncInterval;

        RpcSync.Invoke((Id, epoch, lastSequence + 1, sendQueue.ToArray()));
        lastSequence += sendQueue.Count;
        sendQueue.Clear();
    }

    /// <summary>
    /// 所有者以外として、受け取った位置を順にたどります。遅れているほど速く進んで追いつきます。
    /// </summary>
    private void FollowIncomingPositions(float deltaTime)
    {
        while (incomingQueue.Count > MaxIncomingPositions) incomingQueue.Dequeue();

        float speed = (state == State.Chasing ? CurrentChaseSpeed : CurrentWanderSpeed) * speedMod * (1f + 0.25f * Mathf.Max(0, incomingQueue.Count - 2));
        float moveDistance = speed * deltaTime;
        while (moveDistance > 0f && incomingQueue.Count > 0)
        {
            var target = incomingQueue.Peek();
            var diff = target - position;
            float distance = diff.magnitude;
            if (distance <= moveDistance)
            {
                position = target;
                moveDistance -= distance;
                incomingQueue.Dequeue();
            }
            else
            {
                position += diff / distance * moveDistance;
                moveDistance = 0f;
            }
        }
    }

    /// <summary>
    /// 追いかけている自分自身に追いついていれば、キル演出を始めます。
    /// </summary>
    private bool TryCatch(GamePlayer target)
    {
        if (!target.AmOwner || !target.IsActive || meetingCalled || MeetingHud.Instance) return false;
        if (((Vector2)target.TruePosition - position).magnitude > CatchDistance) return false;

        RpcCatch.Invoke((Id, epoch + 1, target));
        return true;
    }

    /// <summary>
    /// 被害者視点の捕獲演出を再生します。
    /// バブルガンの演出(PlayCinematicKill)と違い、待ち時間を置かずに即座にキルして演出を始めます。
    /// </summary>
    /// <param name="victim">キルするプレイヤー</param>
    private void PlayCatchAnimation(GamePlayer victim)
    {
        if (state is State.Catching or State.Vanishing) return;

        ChangeState(State.Catching, victim);

        //被害者視点では、キルモーションの開始と同時にライトを消す
        light.enabled = false;

        //演出の間は動けないようにする
        var frozen = victim.VanillaPlayer;
        if (frozen)
        {
            frozen.NetTransform.Halt();
            frozen.moveable = false;
            NebulaManager.Instance.StartDelayAction(CinematicDuration, () => { if (frozen) frozen.moveable = true; });
        }

        if (Minigame.Instance) Minigame.Instance.ForceClose();
        //風車はプレイヤーではないので、ひとまず被害者自身をキラーとする。演出は自前で出すのでオーバーレイは付けず、死体は残す
        victim.MurderPlayer(victim, CaughtState, EventDetail.Kill, KillParameter.WithAssigningGhostRole | KillParameter.WithDeadBody, KillCondition.TargetAlive);

        //演出中の姿勢(顔の揺れなし)にしてから、Bodyの位置に注目させる
        UpdateTransform(0f);
        Vector2 focus = bodyTransform.position;
        NebulaGameManager.Instance!.WideCamera.SetAttention(new SimpleAttention(0f, CinematicView, focus, FunctionalLifespan.GetTimeLifespan(CinematicDuration)));

        screenHands = new CatchScreenHands(Sprites.GetSprite(HandRecognizedIndex));

        var overlayPrefab = UnityHelper.CreateObject<CustomKillOverlay>("overlayPrefab", null, Vector3.zero);
        HudManager.Instance.KillOverlay.ShowKillAnimation(overlayPrefab, new CustomKillOverlayData(overlayPrefab.gameObject).VanillaData);
    }

    private bool TrySetPath(Vector2 destination)
    {
        var navPath = NavVerticesHelpers.CalcPath(position, destination, PathRadius);
        if (navPath == null || navPath.Path.Length == 0) return false;

        path = navPath.Path;
        nextPathIndex = 0;
        return true;
    }

    private void UpdateTransform(float deltaTime)
    {
        bool recognized = state != State.Wandering;

        //プレイヤーと同様にyに応じてzを決め、前後関係を表現する
        //被害者視点の演出中は、体がオーバーレイより前面に来るようにする
        float z = state == State.Catching ? CinematicZ - BodyZ : position.y / 1000f;
        root.transform.position = new Vector3(position.x, position.y, z);

        //認識後の顔は揺らさない
        float bounce = recognized ? 0f : Mathf.Abs(Mathf.Sin(time * 8f)) * 0.08f;
        bodyTransform.localPosition = new Vector3(0f, BodyHeight + bounce, BodyZ);

        //消滅時は手をまとめて縮めてから、全体を縮める
        float handsScale = 1f, bodyScale = 1f;
        if (state == State.Vanishing)
        {
            handsScale = 1f - SmoothStep(stateTime / ShrinkDuration);
            bodyScale = 1f - SmoothStep((stateTime - ShrinkDuration) / ShrinkDuration);
        }
        wheelTransform.localScale = new Vector3(handsScale, handsScale, 1f);
        //迫ってくる拡大は、カメラの注目点である体の中心を基準にかける(足元基準だと拡大につれて上にずれる)
        float approachScale = state == State.Catching ? GetApproachScale(stateTime) : 1f;
        bodyTransform.localScale = new Vector3(bodyScale * approachScale, bodyScale * approachScale, 1f);
        light.transform.localScale = new Vector3(lightScale.x * bodyScale, lightScale.y * bodyScale, lightScale.z);

        wheelAngle = (wheelAngle + GetRotateSpeed() * deltaTime) % 360f;
        UpdateHands(wheelAngle, recognized);
    }

    /// <summary>
    /// 被害者視点で、オーバーレイより前面に出て迫ってくる拡大率を返します。
    /// じわじわ大きくなる分に、段階ごとの飛び出し(少し行き過ぎて戻る)を掛け合わせます。
    /// </summary>
    static private float GetApproachScale(float time)
    {
        float scale = 1f + ApproachCreep * time;
        foreach (var stepTime in ApproachStepTimes)
            scale *= Mathf.Pow(ApproachStepFactor, EaseOutBack(Mathf.Clamp01((time - stepTime) / ApproachStepDuration)));
        return scale;
    }

    /// <summary>
    /// 顔をHUDに移します。ワールド上の顔と同じ見た目の大きさから現れるよう、両カメラの映す範囲の比で大きさを換算します。
    /// </summary>
    private void ShowHudFace()
    {
        if (screenHands == null) return;
        float worldOrthographicSize = NebulaGameManager.Instance!.WideCamera.Camera.orthographicSize;
        float hudOrthographicSize = HudManager.Instance.UICamera.orthographicSize;
        screenHands.ShowFace(Sprites.GetSprite(FaceRecognizedIndex), bodyTransform.lossyScale.x * hudOrthographicSize / worldOrthographicSize);
        face.enabled = false;
    }

    /// <summary>
    /// 迫ってきた段階(0から4)を返します。
    /// </summary>
    static private int GetApproachLevel(float time) => ApproachStepTimes.Count(stepTime => time >= stepTime) + (time >= HudFaceTime ? 1 : 0);

    static private float EaseOutBack(float p)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float q = p - 1f;
        return 1f + c3 * q * q * q + c1 * q * q;
    }

    static private float SmoothStep(float p)
    {
        p = Mathf.Clamp01(p);
        return p * p * (3f - 2f * p);
    }

    private float GetRotateSpeed()
    {
        if (state == State.Wandering) return WheelRotateSpeed;
        if (IsWindingUp) return 0f;

        float t = ChaseTime % ChaseRotateRhythmLength;
        foreach (var (duration, fast) in ChaseRotateRhythm)
        {
            if (t < duration) return fast ? WheelFastRotateSpeed : WheelRotateSpeed;
            t -= duration;
        }
        return WheelRotateSpeed;
    }

    private bool IsWindingUp => state == State.Chasing && stateTime < currentWindup;

    //追いかけ始めてからの時間(引き継いだ場合はそれまでの分を含み、構えている間は進まない)。被害者視点の演出中は、その開始からの時間
    private float ChaseTime => state == State.Chasing ? chaseTimeOffset + Mathf.Max(0f, stateTime - currentWindup) : stateTime;

    //追いかけ続けてしばらく経つとスピードアップする。各クライアントで状態の経過時間から判断する
    private bool IsSpedUp => state == State.Chasing && ChaseTime >= ChaseSpeedUpTime;
    private bool IsIgnoringWalls => state == State.Chasing && ChaseTime >= ChaseIgnoreWallsTime;
    private int ChasePhase => IsIgnoringWalls ? 2 : IsSpedUp ? 1 : 0; //ChaseSpeedsの添字
    private float CurrentChaseSpeed => (isGameMode ? ModeChaseSpeeds : ChaseSpeeds)[ChasePhase];
    //今のチェイスで追いかけ続けられる時間。制限がなければnull
    private float? ChaseDurationLimit => isGameMode && chaseCount >= 1 && chaseCount <= ModeChaseDurations.Length ? ModeChaseDurations[chaseCount - 1] : null;

    /// <summary>
    /// 突き刺して引く動きの形を返します。
    /// sin(π√p) は p=0 から一気に立ち上がって p=0.25 で最大となり、その後ゆっくり0に戻ります。
    /// </summary>
    static private float ThrustCurve(float p) => Mathf.Sin(Mathf.PI * Mathf.Sqrt(Mathf.Clamp01(p)));

    /// <summary>
    /// 手iの突き刺し具合を0から1で返します。
    /// 構えている間は手を1つずつ突き出し、追いかけ始めたら全部の手を一斉に繰り返し突き刺します。
    /// </summary>
    private float GetThrust(int handIndex)
    {
        if (state is State.Wandering or State.Vanishing) return 0f;
        if (IsWindingUp)
        {
            //引き継いだときの短い構えでは、同じ動きを速めて収める
            float t = stateTime * (ChaseWindup / currentWindup) - handIndex * WindupPulseInterval;
            return t < 0f || t > WindupPulseDuration ? 0f : ThrustCurve(t / WindupPulseDuration);
        }
        return ThrustCurve((ChaseTime % ThrustInterval) / ThrustInterval);
    }

    /// <summary>
    /// 風車の回転角に合わせて手を配置します。
    /// 手の画像は0番から順に90°ずつ左回りの向きを指しているので、
    /// 回転角を90°単位で丸めた分は画像の差し替えで、端数(±45°)は画像の回転で表現します。
    /// </summary>
    /// <param name="angle">手0の角度(度、左回りが正)</param>
    /// <param name="recognized">認識後は向きによらず一律で🫵を正立させて使う</param>
    private void UpdateHands(float angle, bool recognized)
    {
        int steps = Mathf.RoundToInt(angle / 90f);
        float residual = angle - steps * 90f;

        for (int i = 0; i < hands.Length; i++)
        {
            float thrust = GetThrust(i);
            float distance = HandDistance * (1f + ThrustDistance * thrust);
            float scale = 1f + ThrustScale * thrust;

            //手iが今どの向きの画像に一番近いか(負の角度でも0..3に収める)
            int direction = ((i + steps) % 4 + 4) % 4;
            float rad = (direction * 90f + residual) * Mathf.Deg2Rad;

            var hand = hands[i];
            hand.sprite = Sprites.GetSprite(recognized ? HandRecognizedIndex : direction);
            hand.transform.localPosition = new Vector3(Mathf.Cos(rad) * distance, Mathf.Sin(rad) * distance, 0f);
            hand.transform.localEulerAngles = new Vector3(0f, 0f, recognized ? 0f : residual);
            hand.transform.localScale = new Vector3(scale, scale, 1f);
        }
    }

    void IGameOperator.OnReleased()
    {
        if (root) GameObject.Destroy(root);
        screenHands?.Destroy();
        alarm?.Stop();
    }
}
