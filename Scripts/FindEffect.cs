using UnityEngine;
using Virial;
using Virial.Events.Game;
using Virial.Game;

namespace PsycoWheel;

/// <summary>
/// 「見つけた」ボタンを押したときに、プレイヤーの周囲から飛び出すアイコンです。
/// ブルブル揺れながら現れ、上へ移動しながら消えていきます。
/// </summary>
public class FindEffect : FlexibleLifespan, IGameOperator
{
    private const int SpriteIndex = 7; //‼️
    private const float Duration = 0.9f;
    private const float PopDuration = 0.15f; //現れるまでの時間
    private const float ShakeDuration = 0.4f; //揺れが収まるまでの時間
    private const float ShakeDistance = 0.05f;
    private const float ShakeAngle = 15f;
    private const float RiseDistance = 0.9f;
    private const float FadeStart = 0.5f; //この時間から消え始める
    private const float SpawnRadius = 0.5f; //プレイヤーからこの距離以内に出す
    private const float Scale = 0.8f;

    private readonly GameObject obj;
    private readonly SpriteRenderer renderer;
    private readonly Vector2 basePosition;
    private float time = 0f;

    public FindEffect(Vector2 playerCenter) : base(NebulaAPI.CurrentGame!)
    {
        basePosition = playerCenter + Random.insideUnitCircle * SpawnRadius;

        obj = new GameObject("PsychoWheelFindEffect");
        renderer = obj.AddComponent<SpriteRenderer>();
        renderer.sprite = PsychoWheel.Sprites.GetSprite(SpriteIndex);
        UpdateTransform();
    }

    //見た目だけの処理なので、毎フレーム呼ばれるHUDの更新で滑らかに動かす
    void OnUpdate(GameHudUpdateEvent ev)
    {
        if (!obj) return;

        time += ev.DeltaTime;
        if (time > Duration)
        {
            Release();
            return;
        }
        UpdateTransform();
    }

    private void UpdateTransform()
    {
        float p = time / Duration;

        //揺れは時間とともに弱まる
        float shake = Mathf.Max(0f, 1f - time / ShakeDuration);
        float shakeX = Mathf.Sin(time * 80f) * ShakeDistance * shake;
        float angle = Mathf.Sin(time * 70f) * ShakeAngle * shake;

        //上昇は最初が速く、だんだんゆっくりになる
        float rise = RiseDistance * (1f - (1f - p) * (1f - p));

        float pop = EaseOutBack(Mathf.Clamp01(time / PopDuration)) * Scale;
        float alpha = 1f - SmoothStep((time - FadeStart) / (Duration - FadeStart));

        var pos = basePosition + new Vector2(shakeX, rise);
        obj.transform.position = new Vector3(pos.x, pos.y, -1f); //プレイヤーより手前に出す
        obj.transform.localEulerAngles = new Vector3(0f, 0f, angle);
        obj.transform.localScale = new Vector3(pop, pop, 1f);
        renderer.color = new UnityEngine.Color(1f, 1f, 1f, alpha);
    }

    static private float SmoothStep(float p)
    {
        p = Mathf.Clamp01(p);
        return p * p * (3f - 2f * p);
    }

    static private float EaseOutBack(float p)
    {
        const float c1 = 1.70158f;
        const float c3 = c1 + 1f;
        float q = p - 1f;
        return 1f + c3 * q * q * q + c1 * q * q;
    }

    void IGameOperator.OnReleased()
    {
        if (obj) GameObject.Destroy(obj);
    }
}
