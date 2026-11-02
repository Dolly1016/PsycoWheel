using Nebula.Game;
using Nebula.Patches;
using Nebula.Modules;
using UnityEngine;
using Virial;
using Virial.Attributes;
using Virial.DI;
using Virial.Events.Game;
using Virial.Game;
using Virial.Media;
using Virial.Runtime;
using Virial.Text;

namespace PsycoWheel;

/// <summary>
/// ロビーの右の箱に貼る、キチガイ風車の説明書きです。
/// 近づいて使うと、キチガイ風車の説明を書いたウィンドウを開きます。
/// </summary>
[NebulaPreprocess(PreprocessPhase.PostLoadAddons)]
public class LobbyInformation : AbstractModule<Virial.Game.Game>
{
    static void Preprocess(NebulaPreprocessor preprocessor)
    {
        preprocessor.DIManager.RegisterModule<Virial.Game.Game>(() => new LobbyInformation());
    }

    static private readonly Image Sprite = NebulaAPI.AddonAsset.GetResource("LobbyInformation.png")!.AsImage(100f)!;

    //説明ウィンドウの画像。透かしはウィンドウの右下に薄く表示する
    static private readonly Image WatermarkImage = NebulaAPI.AddonAsset.GetResource("InformationWatermark.png")!.AsImage(100f)!;
    static private readonly Image NormalImage = NebulaAPI.AddonAsset.GetResource("InformationNormal.png")!.AsImage(100f)!;
    static private readonly Image RecognizedImage = NebulaAPI.AddonAsset.GetResource("InformationRecognized.png")!.AsImage(100f)!;
    static private readonly Image FindButtonImage = NebulaAPI.AddonAsset.GetResource("FindButton.png")!.AsImage(100f)!;

    static private readonly Vector3 Position = new(1.59f, 1.06f, 0.0009f); //右の箱の正面

    //ウィンドウの大きさ。ヘルプ画面(7.8×4.7)より少し小さくする
    private const float WindowWidth = 7.4f;
    private const float WindowHeight = 4.5f;
    private const float ScrollViewWidth = 7.1f;
    private const float ScrollViewHeight = 3.9f;
    private const float TextWidth = ScrollViewWidth - 0.4f;

    //キャプション付きの画像(役職説明と同じ並び)
    static private readonly Virial.Compat.FuzzySize CaptionImageSize = new(0.55f, 0.65f);
    private const float CaptionImageMargin = 0.15f;
    private const float CaptionTextWidth = TextWidth - 0.55f - CaptionImageMargin;

    protected override void OnInjected(Virial.Game.Game container)
    {
        //ロビーはゲームの生成と同じフレームに作られることも、遅れて作られることもある。
        //ゲームが始まるまで毎ティック確かめ、ロビーがあればコンソールを置いて確かめるのをやめる
        bool created = false;
        GameOperatorManager.Instance?.Subscribe<UpdateEvent>(_ =>
        {
            var lobby = LobbyBehaviour.Instance;
            if (!lobby) return;
            created = true;
            CreateConsole(lobby);
        }, new FunctionalLifespan(() => !created && (container as NebulaGameManager)?.GameState == NebulaGameStates.NotStarted));
    }

    static private void CreateConsole(LobbyBehaviour lobby)
    {
        //マーケットプレイスと同じく、着替えのコンソールを複製して置く
        var console = LobbyConsoles.Create(lobby, Position, OpenWindow);
        if (console == null) return;
        console.transform.position = Position;
        console.name = "PsychoWheelInformation";
        console.Outline.sprite = Sprite.GetSprite();
    }

    //開いている説明ウィンドウ。開いている間は新たに開かない
    static private MetaScreen? lastWindow = null;

    static private void OpenWindow()
    {
        if (lastWindow) return;

        var gui = NebulaAPI.GUI;

        //スクロールビューがマスクを使うので、ウィンドウ側ではマスクを使わない。
        //スクロールビューの外に出すタイトルは、マスクを使わない文字で表示する
        var window = lastWindow = MetaScreen.GenerateWindow(new(WindowWidth, WindowHeight), HudManager.Instance.transform, Vector3.zero, true, true, false, BackgroundSetting.Modern);
        var titleAttr = new TextAttribute(gui.GetAttribute(AttributeParams.StandardBaredBoldLeft)) { FontSize = new(2.2f, false), Size = new(TextWidth, 0.5f) };

        //本文は折り返した文章の高さに合わせて縮める
        var subtitleAttr = new TextAttribute(gui.GetAttribute(AttributeAsset.DocumentSubtitle1)) { FontSize = new(1.9f, false), IsFlexible = true, Size = new(TextWidth, 0.5f) };
        var contentAttr = new TextAttribute(gui.GetAttribute(AttributeAsset.DocumentStandard)) { FontSize = new(1.4f, false), Wrapping = true, IsFlexible = true, Size = new(TextWidth, 100f) };
        var captionAttr = new TextAttribute(contentAttr) { Size = new(CaptionTextWidth, 100f) };

        GUIWidget CaptionedImage(Image image, string translationKey) => gui.HorizontalHolder(GUIAlignment.Left,
            gui.Image(GUIAlignment.Left, image, CaptionImageSize),
            gui.HorizontalMargin(CaptionImageMargin),
            gui.LocalizedText(GUIAlignment.Left, captionAttr, translationKey));

        var document = gui.VerticalHolder(GUIAlignment.Left,
            gui.LocalizedText(GUIAlignment.Left, contentAttr, "psychoWheel.information.intro"),
            gui.VerticalMargin(0.15f),
            CaptionedImage(NormalImage, "psychoWheel.information.image.normal"),
            gui.VerticalMargin(0.1f),
            CaptionedImage(RecognizedImage, "psychoWheel.information.image.recognized"),
            gui.VerticalMargin(0.1f),
            CaptionedImage(FindButtonImage, "psychoWheel.information.image.findButton"),
            gui.VerticalMargin(0.25f),
            gui.LocalizedText(GUIAlignment.Left, subtitleAttr, "psychoWheel.information.normal"),
            gui.VerticalMargin(0.05f),
            gui.LocalizedText(GUIAlignment.Left, contentAttr, "psychoWheel.information.normal.content"),
            gui.VerticalMargin(0.25f),
            gui.LocalizedText(GUIAlignment.Left, subtitleAttr, "psychoWheel.information.gameMode"),
            gui.VerticalMargin(0.05f),
            gui.LocalizedText(GUIAlignment.Left, contentAttr, "psychoWheel.information.gameMode.content")
            );

        window.SetWidget(gui.VerticalHolder(GUIAlignment.Center,
            gui.LocalizedText(GUIAlignment.Left, titleAttr, "psychoWheel.information.title"),
            gui.VerticalMargin(0.1f),
            gui.ScrollView(GUIAlignment.Center, new(ScrollViewWidth, ScrollViewHeight), null, document, out _)
            ), new Vector2(0.5f, 1f), WatermarkImage, out _);
    }
}
