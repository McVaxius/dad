namespace dad.Models;

public readonly record struct DadVisibleRunState(
    DadRunResult LocalRun,
    DadRunResult AuthorityRun,
    DadRunResult VisibleRun,
    bool IsRemoteAuthorityView,
    DadAuthorityViewState AuthorityView);
