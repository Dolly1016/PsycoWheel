using System.Collections.Generic;
using Nebula.Extensions;
using UnityEngine;

namespace PsycoWheel;

/// <summary>
/// 被害者視点の捕獲演出で、画面の周囲に次々と現れて突き刺してくる手を管理します。
/// HUDの子として配置するので、ズームによらず画面に張り付いて見えます。
/// </summary>
public class CatchScreenHands
{
    private const float HandZ = 20f; //HUD上の奥行き
    private const float PopDuration = 0.12f; //出現時に飛び出してくるまでの時間
    private const float ThrustInterval = 0.3f;
    private const float ThrustScale = 0.3f;
    static private readonly Vector2 OuterHalfSize = new(4.8f, 2.7f); //出現させる範囲(HUD上の原点からの距離)
    static private readonly Vector2 InnerHalfSize = new(1.8f, 1.4f); //画面中央の顔を埋めないよう、この範囲には出さない
    private const float ExclusionFadeDuration = 0.4f; //最終段階に入ってから、中央を避ける範囲が無くなるまでの時間
    private const float ShrinkDuration = 0.3f;

    //迫ってくる段階(0から4)ごとの、1秒あたりの出現数。最初と最後の間を一定の比率で増やす
    static private readonly float[] SpawnRates = [3f, 5.4f, 9.8f, 17.7f, 32f];
    //迫ってくる段階(0から4)ごとの、出現する手の大きさ。最後は画面を埋め尽くして上書きしていく
    static private readonly float[] SizeScales = [1f, 1.3f, 1.65f, 2.1f, 2.6f];

    //最終段階でHUDに出す顔
    private const float FaceZ = HandZ - 1f; //手より手前
    private const float FacePopScale = 1.4f; //出現時に、それまでの見た目の大きさからこの倍率までぐっと拡大する
    private const float FacePopDuration = 0.15f;
    private const float MinHandScale = 2.64f;
    private const float MaxHandScale = 4.4f;

    private class Hand
    {
        public Transform Transform = null!;
        public float Time;
        public float BaseScale;
        public float Phase;
    }

    private readonly Sprite sprite;
    private readonly GameObject holder;
    private readonly List<Hand> hands = [];
    private float spawnProgress = 0f;
    private float finalLevelTime = 0f; //最終段階に入ってからの時間
    private float? shrinkTime = null;
    private Transform? face = null;
    private float faceTime = 0f;
    private float faceBaseScale = 1f;

    public CatchScreenHands(Sprite sprite)
    {
        this.sprite = sprite;
        holder = new GameObject("PsychoWheelScreenHands");
        holder.layer = LayerExpansion.GetUILayer(); //HUDカメラに映すためUIレイヤーに置く
        holder.transform.SetParent(HudManager.Instance.transform, false);
        holder.transform.localPosition = Vector3.zero;
    }

    /// <param name="level">迫ってきた段階(0から4)。大きいほど多く出現する</param>
    public void Update(float deltaTime, int level)
    {
        if (!holder) return;
        level = Mathf.Clamp(level, 0, SpawnRates.Length - 1);
        if (level == SpawnRates.Length - 1) finalLevelTime += deltaTime;

        if (shrinkTime == null)
        {
            spawnProgress += SpawnRates[level] * deltaTime;
            while (spawnProgress >= 1f)
            {
                spawnProgress -= 1f;
                Spawn(level);
            }
        }
        else
        {
            shrinkTime += deltaTime;
            if (shrinkTime > ShrinkDuration)
            {
                Destroy();
                return;
            }
        }

        float shrink = shrinkTime.HasValue ? 1f - SmoothStep(shrinkTime.Value / ShrinkDuration) : 1f;
        foreach (var hand in hands)
        {
            hand.Time += deltaTime;
            float pop = EaseOutBack(Mathf.Clamp01(hand.Time / PopDuration));
            float p = ((hand.Time + hand.Phase) % ThrustInterval) / ThrustInterval;
            float thrust = Mathf.Sin(Mathf.PI * Mathf.Sqrt(p));
            float scale = hand.BaseScale * pop * (1f + ThrustScale * thrust) * shrink;
            hand.Transform.localScale = new Vector3(scale, scale, 1f);
        }

        if (face)
        {
            faceTime += deltaTime;
            float pop = 1f + (FacePopScale - 1f) * EaseOutBack(Mathf.Clamp01(faceTime / FacePopDuration));
            float scale = faceBaseScale * pop * shrink;
            face!.localScale = new Vector3(scale, scale, 1f);
        }
    }

    /// <summary>
    /// 画面中央、手より手前に顔を出します。
    /// </summary>
    /// <param name="baseScale">出現時の大きさ。これまでの見た目と揃えると、そこからぐっと拡大して見える</param>
    public void ShowFace(Sprite faceSprite, float baseScale)
    {
        if (!holder || face) return;

        var obj = new GameObject("Face");
        obj.layer = LayerExpansion.GetUILayer();
        obj.transform.SetParent(holder.transform, false);
        obj.transform.localPosition = new Vector3(0f, 0f, FaceZ);
        obj.transform.localScale = new Vector3(baseScale, baseScale, 1f);
        obj.AddComponent<SpriteRenderer>().sprite = faceSprite;
        face = obj.transform;
        faceBaseScale = baseScale;
        faceTime = 0f;
    }

    private void Spawn(int level)
    {
        //HUDは原点を中心に映るので、外側の矩形の中から、中央の矩形を避けてランダムに選ぶ
        //最終段階に入ったら、中央を避ける範囲を徐々に狭めて最後は画面全体を覆う
        var innerHalfSize = InnerHalfSize * (1f - Mathf.Clamp01(finalLevelTime / ExclusionFadeDuration));
        Vector2 pos;
        do pos = new(Random.Range(-OuterHalfSize.x, OuterHalfSize.x), Random.Range(-OuterHalfSize.y, OuterHalfSize.y));
        while (Mathf.Abs(pos.x) < innerHalfSize.x && Mathf.Abs(pos.y) < innerHalfSize.y);

        var obj = new GameObject("Hand");
        obj.layer = LayerExpansion.GetUILayer();
        obj.transform.SetParent(holder.transform, false);
        //後から出た手ほど手前に重ねる
        obj.transform.localPosition = new Vector3(pos.x, pos.y, HandZ - hands.Count * 0.001f);
        obj.transform.localEulerAngles = new Vector3(0f, 0f, Random.Range(-25f, 25f));
        obj.transform.localScale = Vector3.zero;
        obj.AddComponent<SpriteRenderer>().sprite = sprite;

        hands.Add(new() { Transform = obj.transform, Time = 0f, BaseScale = Random.Range(MinHandScale, MaxHandScale) * SizeScales[level], Phase = Random.Range(0f, ThrustInterval) });
    }

    /// <summary>
    /// 出現を止め、その場で縮小して消えます。
    /// </summary>
    public void Shrink()
    {
        shrinkTime ??= 0f;
    }

    public void Destroy()
    {
        if (holder) GameObject.Destroy(holder);
        hands.Clear();
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
}
