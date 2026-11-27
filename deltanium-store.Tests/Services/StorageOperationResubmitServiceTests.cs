using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using DeltaniumStore.Models;
using DeltaniumStore.Services;
using deltanium_store.Tests.Helpers;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace deltanium_store.Tests.Services;

public class StorageOperationResubmitServiceTests
{
    private const string App = "03699b56070f41068ca1166169547a4bd05022c41e184069352163dfe21553a14c";
    private readonly Mock<IStorageContractService> _contracts = new();
    private readonly Mock<IFileStorageService> _files = new();
    private readonly Mock<IBlockchainService> _chain = new();
    private readonly List<StorageContract> _pending = new();

    private StorageOperationResubmitService Build()
    {
        _contracts.Setup(c => c.ListUnsubmittedContractsAsync()).ReturnsAsync(() => new List<StorageContract>(_pending));
        _contracts.Setup(c => c.UpdateContractAsync(It.IsAny<StorageContract>())).ReturnsAsync((StorageContract c) => c);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        return new StorageOperationResubmitService(_contracts.Object, _files.Object, _chain.Object, config,
            NullLogger<StorageOperationResubmitService>.Instance);
    }

    private (StorageContract Contract, FileMetadata File) Pending(long createdAt, bool marked = true, string type = "post")
    {
        var c = TestDataBuilder.CreateStorageContract(appPublicKey: App, startDateUnix: createdAt);
        c.CreatedAtUnix = createdAt;
        var f = TestDataBuilder.CreateFileMetadata(recipientPubKey: App, type: type);
        f.ContractId = c.ContractId;
        if (marked) c.PendingSubmitFileId = f.FileId;
        _pending.Add(c);
        _files.Setup(x => x.ListMetadataByContractAsync(c.ContractId)).ReturnsAsync(new List<FileMetadata> { f });
        return (c, f);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public async Task Failed_contract_is_resubmitted_and_marked()
    {
        var (c, f) = Pending(Now - 600);
        _chain.Setup(x => x.IsTxKnownAsync(It.IsAny<string>())).ReturnsAsync(false);
        _chain.Setup(x => x.SubmitStorageOperationAsync(f, c)).ReturnsAsync(new SubmitResult(true));

        (await Build().RunOnceAsync(Now)).Should().Be(1);

        c.BlockchainSubmittedAtUnix.Should().BeGreaterThan(0);
        c.SubmitAttempts.Should().Be(1);
        _chain.Verify(x => x.IsTxKnownAsync(BlockchainService.StorageOperationTxId(c.ContractId)), Times.Once);
    }

    [Fact]
    public async Task Contract_already_on_chain_is_marked_without_a_second_tx()
    {
        var (c, _) = Pending(Now - 600);
        _chain.Setup(x => x.IsTxKnownAsync(It.IsAny<string>())).ReturnsAsync(true);

        (await Build().RunOnceAsync(Now)).Should().Be(1);

        c.BlockchainSubmittedAtUnix.Should().BeGreaterThan(0);
        _chain.Verify(x => x.SubmitStorageOperationAsync(It.IsAny<FileMetadata>(), It.IsAny<StorageContract>()), Times.Never);
    }

    [Fact]
    public async Task Fresh_contract_is_left_to_the_upload_in_progress()
    {
        Pending(Now - 10);
        (await Build().RunOnceAsync(Now)).Should().Be(0);
        _chain.Verify(x => x.IsTxKnownAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Unreachable_blocker_stops_the_pass_and_changes_nothing()
    {
        var (c, _) = Pending(Now - 600);
        Pending(Now - 500);
        _chain.Setup(x => x.IsTxKnownAsync(It.IsAny<string>())).ReturnsAsync((bool?)null);

        (await Build().RunOnceAsync(Now)).Should().Be(0);

        _chain.Verify(x => x.IsTxKnownAsync(It.IsAny<string>()), Times.Once);
        c.SubmitAttempts.Should().Be(0);
        c.BlockchainSubmittedAtUnix.Should().BeNull();
    }

    [Fact]
    public async Task Failure_records_the_error_and_backs_off()
    {
        var (c, f) = Pending(Now - 600);
        _chain.Setup(x => x.IsTxKnownAsync(It.IsAny<string>())).ReturnsAsync(false);
        _chain.Setup(x => x.SubmitStorageOperationAsync(f, c)).ReturnsAsync(new SubmitResult(false, "refused"));
        var svc = Build();

        (await svc.RunOnceAsync(Now)).Should().Be(0);
        c.LastSubmitError.Should().Be("refused");
        c.SubmitAttempts.Should().Be(1);

        svc.IsDue(c, Now + 60).Should().BeFalse();
        svc.IsDue(c, Now + 301).Should().BeTrue();
        c.SubmitAttempts = 1000;
        svc.IsDue(c, Now + 6 * 3600 - 10).Should().BeFalse(); // capped at 6 h
        svc.IsDue(c, Now + 6 * 3600 + 1).Should().BeTrue();
    }

    [Fact]
    public async Task Unmarked_new_contract_is_never_submitted()
    {
        // No marker means the upload never passed the submit checks (e.g. a file not of the contract's App).
        Pending(Math.Max(Now, StorageOperationResubmitService.LegacyBeforeUnix) - 600, marked: false);
        (await Build().RunOnceAsync(Math.Max(Now, StorageOperationResubmitService.LegacyBeforeUnix))).Should().Be(0);
        _chain.Verify(x => x.IsTxKnownAsync(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task Legacy_contract_is_matched_only_to_a_post_of_its_app()
    {
        var created = StorageOperationResubmitService.LegacyBeforeUnix - 86400;
        var (c, f) = Pending(created, marked: false);
        _chain.Setup(x => x.IsTxKnownAsync(It.IsAny<string>())).ReturnsAsync(false);
        _chain.Setup(x => x.SubmitStorageOperationAsync(f, c)).ReturnsAsync(new SubmitResult(true));

        (await Build().RunOnceAsync(Now)).Should().Be(1);
        c.PendingSubmitFileId.Should().Be(f.FileId);

        _pending.Clear();
        var (other, of) = Pending(created, marked: false);
        of.RecipientPubKey = "03ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff";
        (await Build().RunOnceAsync(Now)).Should().Be(0);
        other.BlockchainSubmittedAtUnix.Should().BeNull();
    }

    [Fact]
    public async Task Attached_files_under_their_own_contract_are_resubmitted_like_posts()
    {
        // Every stored item has its own contract now, attached files and chat messages included.
        var (c, f) = Pending(Now - 600, type: "file");
        _chain.Setup(x => x.IsTxKnownAsync(It.IsAny<string>())).ReturnsAsync(false);
        _chain.Setup(x => x.SubmitStorageOperationAsync(f, c)).ReturnsAsync(new SubmitResult(true));

        (await Build().RunOnceAsync(Now)).Should().Be(1);
        c.BlockchainSubmittedAtUnix.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Legacy_contracts_never_pick_a_plain_file()
    {
        Pending(StorageOperationResubmitService.LegacyBeforeUnix - 86400, marked: false, type: "file");
        (await Build().RunOnceAsync(Now)).Should().Be(0);
        _chain.Verify(x => x.SubmitStorageOperationAsync(It.IsAny<FileMetadata>(), It.IsAny<StorageContract>()), Times.Never);
    }

    [Fact]
    public void Tx_id_is_stable_per_contract()
    {
        var a = BlockchainService.StorageOperationTxId("945ee3954db5411da11db564486a880a");
        a.Should().HaveLength(32).And.MatchRegex("^[0-9a-f]{32}$");
        BlockchainService.StorageOperationTxId("945ee3954db5411da11db564486a880a").Should().Be(a);
        BlockchainService.StorageOperationTxId("other").Should().NotBe(a);
    }
}
