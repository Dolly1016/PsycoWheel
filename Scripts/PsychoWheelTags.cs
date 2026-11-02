using Nebula.Utilities;
using Virial.Attributes;

namespace PsycoWheel;

/// <summary>
/// 死因などの翻訳タグ。プリロード中に生成する必要がある。
/// </summary>
[NebulaPreprocess(PreprocessPhase.PreRoles)]
public static class PsychoWheelTags
{
    public static TranslatableTag Caught = new("state.psychoWheel");
}
