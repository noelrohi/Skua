using Skua.Core.ViewModels;

namespace Skua.Avalonia.Tests;

/// <summary>The Script Repo's search, ported from the Windows view.</summary>
public sealed class ScriptRepoSearchTests
{
    private static readonly ScriptInfoViewModel Leveling = ScriptsPanelTests.Info("Farm/Leveling.cs", "Leveling", "Levels up to 100.", "farm", "xp");
    private static readonly ScriptInfoViewModel Gold = ScriptsPanelTests.Info("Farm/Gold.cs", "Gold", "Farms gold, fast.", "farm", "gold");
    private static readonly ScriptInfoViewModel Nulgath = ScriptsPanelTests.Info("Nation/Nulgath.cs", "Nulgath Bags", "For the farm of Nulgath.", "nation");
    private static readonly ScriptInfoViewModel[] All = [Nulgath, Leveling, Gold];

    [Fact]
    public void No_query_keeps_every_Script_in_the_view_models_order() =>
        Assert.Equal(All, ScriptRepoSearch.Apply(All, "  ", ScriptSearchScope.All));

    [Fact]
    public void All_ranks_a_name_match_then_a_description_then_a_path_then_by_name()
    {
        Assert.Equal([Gold, Nulgath, Leveling], ScriptRepoSearch.Apply(All, "farm", ScriptSearchScope.All));
        ScriptInfoViewModel helper = ScriptsPanelTests.Info("Other/Helper.cs", "Farm Helper");
        Assert.Equal([helper, Gold], ScriptRepoSearch.Apply([Gold, helper], "farm", ScriptSearchScope.All));
    }

    [Fact]
    public void All_matches_names_descriptions_tags_and_the_Script_Source_path()
    {
        Assert.Equal([Gold], ScriptRepoSearch.Apply(All, "gold", ScriptSearchScope.All));
        Assert.Equal([Nulgath], ScriptRepoSearch.Apply(All, "nation/", ScriptSearchScope.All));
        Assert.Equal([Leveling], ScriptRepoSearch.Apply(All, "xp", ScriptSearchScope.All));
    }

    [Fact]
    public void Every_word_has_to_match_as_in_skua_scripts_search() =>
        Assert.Equal([Gold], ScriptRepoSearch.Apply(All, "farm fast", ScriptSearchScope.All));

    [Fact]
    public void A_scope_looks_only_at_its_field()
    {
        Assert.Empty(ScriptRepoSearch.Apply(All, "fast", ScriptSearchScope.Name));
        Assert.Equal([Gold], ScriptRepoSearch.Apply(All, "fast", ScriptSearchScope.Desc));
        Assert.Equal([Gold, Leveling], ScriptRepoSearch.Apply(All, "farm", ScriptSearchScope.Tag));
        Assert.Equal([Nulgath], ScriptRepoSearch.Apply(All, "Nation/Nulgath.cs", ScriptSearchScope.File));
    }

    [Fact]
    public void Tag_and_file_scopes_rank_exact_then_prefix_then_contained()
    {
        ScriptInfoViewModel farmer = ScriptsPanelTests.Info("Other/Farmer.cs", "Farmer", "", "farmer");
        ScriptInfoViewModel superFarm = ScriptsPanelTests.Info("Other/SuperFarm.cs", "Super", "", "superfarm");
        Assert.Equal([Leveling, farmer, superFarm],
            ScriptRepoSearch.Apply([superFarm, farmer, Leveling], "farm", ScriptSearchScope.Tag));
        Assert.Equal([Gold, Leveling], ScriptRepoSearch.Apply(All, "farm/", ScriptSearchScope.File));
    }
}
