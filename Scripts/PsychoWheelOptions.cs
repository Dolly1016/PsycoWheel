using Nebula.Modules;
using Virial;
using Virial.Attributes;
using Virial.Configuration;
using Virial.Game;

namespace PsycoWheel;

/// <summary>
/// 「各種設定」に追加するキチガイ風車設定です。
/// </summary>
//タブ(ConfigurationTab)はPreRolesで作られるため、NoSの各種設定と同じくPostRolesで作る
[NebulaPreprocess(PreprocessPhase.PostRoles)]
public static class PsychoWheelOptions
{
    //キチガイ風車モードでは必ず出現させるので、出現させるかどうかの設定は隠す
    static public readonly BoolConfiguration SpawnOption = NebulaAPI.Configurations.Configuration("options.psychoWheel.spawn", false, () => !PsychoWheelGameMode.IsSelected);
    static public readonly IntegerConfiguration NumOfWheelsOption = NebulaAPI.Configurations.Configuration("options.psychoWheel.numOfWheels", (1, 5), 1, () => PsychoWheelGameMode.IsSelected || SpawnOption.GetValue());
    //キチガイ風車モードで、この間隔ごとに風車を1体ずつ増やす。0なら増やさない
    static public readonly FloatConfiguration AddIntervalOption = NebulaAPI.Configurations.Configuration("options.psychoWheel.addInterval", (30f, 300f, 15f), 45f,
        value => value <= 0f ? Language.Translate("options.switch.off") : value + Language.Translate("options.sec"), () => PsychoWheelGameMode.IsSelected);
    static public readonly FloatConfiguration TimeLimitOption = NebulaAPI.Configurations.Configuration("options.psychoWheel.timeLimit", (30f, 900f, 30f), 180f, FloatConfigurationDecorator.Second, () => PsychoWheelGameMode.IsSelected);

    static private readonly IConfigurationHolder Options = NebulaAPI.Configurations.Holder("options.psychoWheel", [ConfigurationTab.Settings], [GameModes.FreePlay, GameModes.Standard, PsychoWheelGameMode.Definition]).AppendConfigurations([
        SpawnOption, NumOfWheelsOption, AddIntervalOption, TimeLimitOption
        ]);

    /// <summary>
    /// タスクフェーズごとに出現させる数。出現させない設定なら0です。
    /// </summary>
    static public int NumOfWheels => PsychoWheelGameMode.IsSelected || SpawnOption.GetValue() ? NumOfWheelsOption.GetValue() : 0;
}
