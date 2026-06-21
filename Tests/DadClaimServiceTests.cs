using dad.Models;
using dad.Services;
using Xunit;

namespace dad.Tests;

// Review M20: the lease/claim collision logic is the core guard against two runs claiming one character.
public sealed class DadClaimServiceTests
{
    private static DadClaimRequestDto Request(string runId, string slotId, string account = "", string character = "", System.DateTime? expires = null)
    {
        var request = new DadClaimRequestDto
        {
            RunId = runId,
            SlotId = slotId,
            RequiredAccountKey = new DadAccountKey(account),
            RequiredCharacterKey = new DadCharacterKey(character),
        };

        // The coordinator issues the lease tagged with the run id (DadClaimService keys collisions off the
        // stored lease's RunId), so mirror that here.
        request.Lease.RunId = runId;
        request.Lease.SlotId = slotId;
        if (expires.HasValue)
            request.Lease.ExpiresUtc = expires.Value;

        return request;
    }

    private static DadParticipantSnapshot Participant(string account, string character, string worker = "w1")
        => new()
        {
            WorkerSessionId = new DadWorkerSessionId(worker),
            ManagedAccountKey = new DadAccountKey(account),
            ActiveCharacterKey = new DadCharacterKey(character),
        };

    [Fact]
    public void GrantsOnCleanSlot()
    {
        var decision = new DadClaimService().TryClaimLocal(Request("run1", "s1"), Participant("acct1", "Aaa@World"));

        Assert.True(decision.Granted);
        Assert.Equal(DadClaimState.Granted, decision.ClaimState);
    }

    [Fact]
    public void CollidesWhenCharacterClaimedByDifferentRun()
    {
        var service = new DadClaimService();
        service.TryClaimLocal(Request("run1", "s1"), Participant("acct1", "Aaa@World"));

        var decision = service.TryClaimLocal(Request("run2", "s2"), Participant("acct1", "Aaa@World"));

        Assert.False(decision.Granted);
        Assert.Equal(DadClaimState.Collided, decision.ClaimState);
    }

    [Fact]
    public void SameRunReclaimIsNotCollision()
    {
        var service = new DadClaimService();
        service.TryClaimLocal(Request("run1", "s1"), Participant("acct1", "Aaa@World"));

        var decision = service.TryClaimLocal(Request("run1", "s1"), Participant("acct1", "Aaa@World"));

        Assert.True(decision.Granted);
        Assert.Equal(DadClaimState.Granted, decision.ClaimState);
    }

    [Fact]
    public void DeniesWrongAccount()
    {
        var decision = new DadClaimService().TryClaimLocal(Request("run1", "s1", account: "acctX"), Participant("acct1", "Aaa@World"));

        Assert.False(decision.Granted);
        Assert.Equal(DadClaimState.Denied, decision.ClaimState);
    }

    [Fact]
    public void DeniesWrongCharacter()
    {
        var decision = new DadClaimService().TryClaimLocal(Request("run1", "s1", character: "Bbb@World"), Participant("acct1", "Aaa@World"));

        Assert.False(decision.Granted);
        Assert.Equal(DadClaimState.Denied, decision.ClaimState);
    }

    [Fact]
    public void DeniesEmptyCharacterKey()
    {
        var decision = new DadClaimService().TryClaimLocal(Request("run1", "s1"), Participant("acct1", string.Empty));

        Assert.False(decision.Granted);
        Assert.Equal(DadClaimState.Denied, decision.ClaimState);
    }

    [Fact]
    public void ReleaseClaimsFreesCharacterForAnotherRun()
    {
        var service = new DadClaimService();
        service.TryClaimLocal(Request("run1", "s1"), Participant("acct1", "Aaa@World"));
        service.ReleaseClaims("run1");

        var decision = service.TryClaimLocal(Request("run2", "s2"), Participant("acct1", "Aaa@World"));

        Assert.True(decision.Granted);
    }

    [Fact]
    public void SweepExpiredLeasesFreesCharacter()
    {
        var service = new DadClaimService();
        var past = System.DateTime.UtcNow.AddSeconds(-5);
        service.TryClaimLocal(Request("run1", "s1", expires: past), Participant("acct1", "Aaa@World"));

        service.SweepExpiredLeases(System.DateTime.UtcNow);

        var decision = service.TryClaimLocal(Request("run2", "s2"), Participant("acct1", "Aaa@World"));
        Assert.True(decision.Granted);
    }
}
