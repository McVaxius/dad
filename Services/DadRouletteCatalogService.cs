using Dalamud.Plugin.Services;
using dad.Models;
using Lumina.Excel.Sheets;

namespace dad.Services;

public sealed class DadRouletteCatalogService
{
    private readonly IDataManager dataManager;

    public DadRouletteCatalogService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public IReadOnlyList<DadPlannerRouletteOption> GetOptions()
    {
        var sheet = dataManager.GetExcelSheet<ContentRoulette>();
        var rows = sheet
            .Where(static row => row.RowId is > 0 and <= byte.MaxValue)
            .Select(static row =>
            {
                var memberType = row.ContentMemberType.ValueNullable;
                return new DadContentRouletteCatalogRow(
                    row.RowId,
                    row.Name.ToString(),
                    row.IsInDutyFinder,
                    row.IsPvP,
                    memberType?.MembersPerParty ?? 0,
                    memberType?.PartyCount ?? 0,
                    row.SortKey,
                    row.QueueMaxPlayers);
            });

        return DadRouletteCatalogProjection.BuildOptions(rows);
    }
}
