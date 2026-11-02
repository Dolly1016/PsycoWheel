using System.Collections.Generic;
using System.Linq;
using Nebula;
using Nebula.Modules;
using UnityEngine;
using Virial;
using Virial.Components;
using Virial.Game;
using Virial.Media;
using GamePlayer = Virial.Game.Player;

namespace PsycoWheel;

/// <summary>
/// 全プレイヤーが持つ、キチガイ風車を見たときの回数とクールダウンを表示するボタンです。
/// 見るには風車を直接クリックします。ボタン自体を押すと、風車をクリックするよう案内します。
/// 見た回数に応じて、クルーメイトはタスクが、キルボタンを持つクルーメイト以外はキルクールダウンが進みます。
/// </summary>
public class PsychoWheelFindButton
{
    static private readonly Image ButtonImage = NebulaAPI.AddonAsset.GetResource("FindButton.png")!.AsImage(115f)!;

    private const float Cooldown = 0.4f;
    //画面の拡大率が1のときに、風車が見つかったとみなす範囲(自分からの距離)
    private const float FindHalfWidth = 5.5f;
    private const float FindHalfHeight = 3.5f;

    private const int CountsPerTask = 50; //クルーメイトがタスクを1個進めるのに必要な回数
    private const int CountsPerKillCooldown = 10; //キルクールダウンを進めるのに必要な回数
    private const float KillCooldownProgress = 5f;

    //回数表示の背景(UsesIcon.pngのコマ)。タスクが進む側はエージェントの代行ボタンと同じ青にする
    private const int DefaultIconVariation = 0;
    private const int TaskIconVariation = 3;

    //押した回数で受ける効果
    private enum Reward
    {
        None,
        Task, //クルーメイト
        KillCooldown, //キルボタンを持つクルーメイト以外
    }

    //押した人の周囲のエフェクトは全員に見せる。見た目だけなので、届かなくてもよいReliableでない通信で送る
    static private readonly RemoteProcess<GamePlayer> RpcShowFindEffect = new("PsychoWheelFindEffect", (player, _) =>
    {
        if (player != null) new FindEffect(player.Position).RegisterSelf();
    }, false);

    private readonly GamePlayer player;
    private readonly IEnumerable<PsychoWheel> wheels;
    private readonly ModAbilityButton button;
    private int count = 0;
    private Reward reward = Reward.None;

    public PsychoWheelFindButton(GamePlayer player, IEnumerable<PsychoWheel> wheels)
    {
        this.player = player;
        this.wheels = wheels;

        button = NebulaAPI.Modules.AbilityButton(NebulaAPI.CurrentGame!, player, false, true, Virial.Compat.VirtualKeyInput.None, null, Cooldown, "psychoWheelFind", ButtonImage,
            _ => IsAnyWheelInSight() && !(PsychoWheelGameMode.Current?.IsTimeUp ?? false), _ => !player.IsDead);
        reward = GetReward();
        //キチガイ風車モードでは見た回数を数えるだけだが、背景は青にする
        button.ShowUsesIcon(PsychoWheelGameMode.IsSelected ? TaskIconVariation : GetIconVariation(reward), count.ToString());
        button.OnClick = _ => DebugScreen.Push(Language.Translate("psychoWheel.clickDirectly"), ClickGuideDuration);
        PsychoWheel.ClickHandler = TryLook;
    }

    private const float ClickGuideDuration = 3f;

    private bool IsCoolingDown => (button.CoolDownTimer?.CurrentTime ?? 0f) > 0f;

    /// <summary>
    /// クリックされた風車を、見られる状態なら見ます。
    /// </summary>
    private void TryLook(PsychoWheel wheel)
    {
        if (button.IsDeadObject || player.IsDead || IsCoolingDown) return;
        if (PsychoWheelGameMode.Current?.IsTimeUp ?? false) return;
        if (!CanLook(wheel, CanSeeChasing)) return;
        Look();
    }

    private void Look()
    {
        //キチガイ風車モードでは効果を受けず、見た回数を数えるだけにする
        //ボタンはゲームモードのモジュールより先に作られることがあるので、モードかどうかは設定で判断する
        if (PsychoWheelGameMode.IsSelected)
        {
            var mode = PsychoWheelGameMode.Current;
            mode?.AddLocalScore();
            button.UpdateUsesIcon((mode?.LocalScore ?? 0).ToString());
            RpcShowFindEffect.Invoke(player);
            button.StartCoolDown();
            return;
        }

        //役職が変わることもあるので、見るたびにどちらの効果を受けるか判定し直す
        //受ける効果が変わったら、それまでの回数は数え直す
        var currentReward = GetReward();
        if (currentReward != reward)
        {
            reward = currentReward;
            count = 0;
            button.ShowUsesIcon(GetIconVariation(reward), count.ToString());
        }

        count++;
        if (GiveReward()) count = 0;
        button.UpdateUsesIcon(count.ToString());
        RpcShowFindEffect.Invoke(player);
        button.StartCoolDown();
    }

    static private int GetIconVariation(Reward reward) => reward == Reward.Task ? TaskIconVariation : DefaultIconVariation;

    private IEnumerable<IKillButtonLike> ShownKillButtons => NebulaAPI.CurrentGame!.KillButtonLikeHandler.KillButtonLike.Where(k => k.IsShown && k.IsAliveObject);

    private Reward GetReward()
    {
        if (player.IsCrewmate) return Reward.Task;
        if (ShownKillButtons.Any()) return Reward.KillCooldown;
        return Reward.None;
    }

    /// <summary>
    /// 画面に収まる範囲に風車がいるか調べます。画面の拡大率に応じて範囲を広げます。
    /// </summary>
    private bool IsAnyWheelInSight()
    {
        bool canSeeChasing = CanSeeChasing;
        return wheels.Any(wheel => CanLook(wheel, canSeeChasing));
    }

    //キチガイ風車モードでは、自分が追いかけられていなければ、誰かを追いかけている風車も見られる
    private bool CanSeeChasing => PsychoWheelGameMode.IsSelected && !wheels.Any(wheel => !wheel.IsDeadObject && wheel.ChaseTarget == player);

    /// <summary>
    /// 風車が見られる状態で、画面に収まる範囲にいるか調べます。画面の拡大率に応じて範囲を広げます。
    /// </summary>
    private bool CanLook(PsychoWheel wheel, bool canSeeChasing)
    {
        //通常は歩き回っている風車だけを対象にする
        if (wheel.IsDeadObject) return false;
        if (!wheel.IsWandering && !(canSeeChasing && wheel.ChaseTarget != null)) return false;

        float screenSize = player.Unbox().CalcAttributeVal(PlayerAttributes.ScreenSize, true);
        var diff = wheel.Position - (Vector2)player.TruePosition;
        return Mathf.Abs(diff.x) <= FindHalfWidth * screenSize && Mathf.Abs(diff.y) <= FindHalfHeight * screenSize;
    }

    /// <summary>
    /// 回数が溜まっていれば効果を与えます。
    /// </summary>
    /// <returns>効果を与えた場合、true</returns>
    private bool GiveReward()
    {
        switch (reward)
        {
            case Reward.Task when count >= CountsPerTask:
                CompleteOneTask();
                return true;
            case Reward.KillCooldown when count >= CountsPerKillCooldown:
                foreach (var killButton in ShownKillButtons)
                    killButton.SetCooldown(Mathf.Max(0f, killButton.Cooldown - KillCooldownProgress));
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 未完了のタスクを1個完了させます。
    /// </summary>
    private void CompleteOneTask()
    {
        var tasks = player.VanillaPlayer.myTasks;
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            if (task.TryCast<NormalPlayerTask>() == null || task.IsComplete) continue;
            player.VanillaPlayer.RpcCompleteTask(task.Id);
            return;
        }
    }
}
