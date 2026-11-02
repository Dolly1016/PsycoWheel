using System;
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Nebula;
using Nebula.Utilities;
using UnityEngine;

namespace PsycoWheel;

/// <summary>
/// 追いかけられている本人の画面に、リアクターのサボタージュ(HudManager.CoReactorFlash)に似た警報を出します。
/// 本物のサボタージュと干渉しないよう、HudManager.FullScreenは使わずに同じ大きさの専用の板を用意します。
/// 本人以外には、画面の明滅なしで音だけを鳴らします。
/// </summary>
internal class ChaseAlarm
{
    private const float FlashInterval = 0.6f; //画面の明滅を切り替える間隔(バニラは1秒)
    private const float SoundInterval = 0.4f; //サボタージュ音を鳴らす間隔(バニラは1秒)
    static private readonly float[] SoundVolumes = [1f, 0.6f]; //交互に鳴らす音量
    private const int NumOfAudioSources = 10; //鳴っている音の音程を後から変えないよう、音ごとに順番に使い回す
    static private readonly Color FlashColor = new(1f, 0f, 0f, 0.372549027f); //バニラと同じ色

    private readonly SpriteRenderer? screen;
    private readonly Func<float>? volumeRate;
    private readonly GameObject soundHolder;
    private readonly AudioSource[] audioSources = new AudioSource[NumOfAudioSources];
    private Coroutine? coroutine;
    private float lowestPitch = 1f; //これまでに鳴らした一番遅い音程。破棄を待つ時間の計算に使う

    /// <summary>
    /// 値があるとき、音を鳴らすたびにこの範囲から音程をランダムに選びます。nullのときは音程を変えません。
    /// </summary>
    public (float min, float max)? PitchRange { get; set; } = null;

    /// <summary>
    /// 音を鳴らす間隔に掛ける倍率です。
    /// </summary>
    public float SoundIntervalRate { get; set; } = 1f;

    /// <param name="withFlash">画面を明滅させる場合、true</param>
    /// <param name="volumeRate">音を鳴らすたびに音量に掛ける倍率。nullなら掛けない</param>
    public ChaseAlarm(bool withFlash, Func<float>? volumeRate)
    {
        this.volumeRate = volumeRate;

        if (withFlash)
        {
            var fullScreen = HudManager.Instance.FullScreen;
            screen = UnityHelper.CreateObject<SpriteRenderer>("PsychoWheelAlarm", fullScreen.transform.parent, fullScreen.transform.localPosition);
            screen.transform.localScale = fullScreen.transform.localScale;
            screen.gameObject.layer = fullScreen.gameObject.layer;
            screen.sprite = fullScreen.sprite;
            screen.sortingOrder = fullScreen.sortingOrder;
            screen.color = FlashColor;
            screen.gameObject.SetActive(false);
        }

        soundHolder = new GameObject("PsychoWheelAlarmSounds");
        soundHolder.transform.SetParent(SoundManager.Instance.transform, false);
        for (int i = 0; i < audioSources.Length; i++)
        {
            var source = soundHolder.AddComponent<AudioSource>();
            source.outputAudioMixerGroup = SoundManager.Instance.SfxChannel;
            source.playOnAwake = false;
            source.loop = false;
            audioSources[i] = source;
        }

        coroutine = NebulaManager.Instance.StartCoroutine(CoFlash().WrapToIl2Cpp());
    }

    private IEnumerator CoFlash()
    {
        float flashTimer = 0f;
        float soundTimer = 0f;
        int soundCount = 0;
        while (true)
        {
            if (screen && flashTimer <= 0f)
            {
                screen!.gameObject.SetActive(!screen.gameObject.activeSelf);
                flashTimer += FlashInterval;
            }
            if (soundTimer <= 0f)
            {
                if (ShipStatus.Instance)
                {
                    var source = audioSources[soundCount % audioSources.Length];
                    source.clip = ShipStatus.Instance.SabotageSound;
                    source.volume = SoundVolumes[soundCount % SoundVolumes.Length] * (volumeRate?.Invoke() ?? 1f);
                    source.pitch = PitchRange is var (min, max) ? UnityEngine.Random.Range(min, max) : 1f;
                    lowestPitch = Mathf.Min(lowestPitch, source.pitch);
                    source.Play();
                    soundCount++;
                }
                soundTimer += SoundInterval * SoundIntervalRate;
            }

            yield return null;
            flashTimer -= Time.deltaTime;
            soundTimer -= Time.deltaTime;
        }
    }

    public void Stop()
    {
        if (coroutine != null && NebulaManager.Instance) NebulaManager.Instance.StopCoroutine(coroutine);
        coroutine = null;
        if (screen) GameObject.Destroy(screen!.gameObject);
        //最後に鳴らした音は途切れさせず、これまでで一番遅い音程でも鳴り終わってから破棄する
        if (soundHolder)
        {
            var clip = ShipStatus.Instance ? ShipStatus.Instance.SabotageSound : null;
            GameObject.Destroy(soundHolder, clip ? clip!.length / lowestPitch : 0f);
        }
    }
}
