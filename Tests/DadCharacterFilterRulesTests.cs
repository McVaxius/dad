using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

public sealed class DadCharacterFilterRulesTests
{
    [Fact]
    public void SearchDataCenterAndWorldFiltersCombineWithExactSelectorsAndCounts()
    {
        var state = new DadCharacterFilterSessionState
        {
            CharacterSearch = "alpha",
            DataCenterName = "Aether",
            WorldName = "Siren",
        };

        var result = DadCharacterFilterRules.Apply(Characters(), state);

        Assert.Equal(5, result.TotalCount);
        Assert.Equal(1, result.ResultCount);
        Assert.Equal("Alpha One@Siren", Assert.Single(result.Characters).CharacterKey);
        Assert.Equal(["Aether", "Crystal", "Materia"], result.DataCenters);
        Assert.Equal(["Adamantoise", "Siren"], result.Worlds);
    }

    [Fact]
    public void DataCenterAndWorldSelectorsAreExactNotSubstringMatches()
    {
        var state = new DadCharacterFilterSessionState { DataCenterName = "Aeth" };
        Assert.Empty(DadCharacterFilterRules.Apply(Characters(), state).Characters);

        state.DataCenterName = "Aether";
        state.WorldName = "Sir";
        Assert.Empty(DadCharacterFilterRules.Apply(Characters(), state).Characters);
    }

    [Fact]
    public void OneSessionStateIsObservedByBothEditorConsumersAndClearResetsIt()
    {
        var shared = new DadCharacterFilterSessionState();
        var mainEditorState = shared;
        var guideEditorState = shared;

        mainEditorState.CharacterSearch = "beta";
        mainEditorState.DataCenterName = "Crystal";
        mainEditorState.WorldName = "Balmung";

        var guideResult = DadCharacterFilterRules.Apply(Characters(), guideEditorState);
        Assert.Equal("Beta Two@Balmung", Assert.Single(guideResult.Characters).CharacterKey);
        Assert.True(guideEditorState.HasFilters);

        guideEditorState.Clear();
        Assert.False(mainEditorState.HasFilters);
        Assert.Equal(5, DadCharacterFilterRules.Apply(Characters(), mainEditorState).ResultCount);
    }

    [Fact]
    public void ChangingDataCenterCanDetectThatTheRetainedWorldMustBeCleared()
    {
        Assert.False(DadCharacterFilterRules.WorldBelongsToDataCenter(Characters(), "Balmung", "Aether"));
        Assert.True(DadCharacterFilterRules.WorldBelongsToDataCenter(Characters(), "Balmung", "Crystal"));
    }

    private static IReadOnlyList<DadAcquiredCharacter> Characters()
        =>
        [
            Character("Alpha One", "Siren", "Aether"),
            Character("Alpha Alt", "Adamantoise", "Aether"),
            Character("Beta Two", "Balmung", "Crystal"),
            Character("Alpha Three", "Balmung", "Crystal"),
            Character("Alpha Oce", "Ravana", "Materia"),
        ];

    private static DadAcquiredCharacter Character(string name, string world, string dataCenter)
        => new()
        {
            CharacterName = name,
            CharacterKey = $"{name}@{world}",
            WorldName = world,
            DataCenterName = dataCenter,
        };
}

