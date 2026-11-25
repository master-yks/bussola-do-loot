using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace FortniteReplayReader.Models.NetFieldExports;

/// <summary>
/// GameMemberInfo gives for each real player their <see cref="TeamIndex"></see> and <see cref="SquadId"/>.
/// Supplies a fallback when the recording omits a player state team index.
/// </summary>
[NetFieldExportGroup("/Script/FortniteGame.GameMemberInfo", minimalParseMode: ParseMode.Normal)]
public class GameMemberInfo : INetFieldExportGroup
{
    [NetFieldExport("SquadId", RepLayoutCmdType.PropertyByte)]
    public byte SquadId { get; set; }

    [NetFieldExport("TeamIndex", RepLayoutCmdType.Enum)]
    public int TeamIndex { get; set; }

    [NetFieldExport("MemberUniqueId", RepLayoutCmdType.PropertyNetId)]
    public string MemberUniqueId { get; set; }
}
