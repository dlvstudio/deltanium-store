using System;
using System.IO;
using System.Linq;
using DeltaniumStore.Services;
using FluentAssertions;
using Xunit;

namespace DeltaniumStore.Tests.Services
{
    public class EgressTrackerTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "egress-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch { }
        }

        [Fact]
        public void Counts_bytes_per_owner_file_and_drain_resets()
        {
            var t = new EgressTracker(_dir);
            t.MapBlock("b1", "file-1");
            t.MapBlock("b2", "file-1");
            t.RecordBlockServed("b1", 100);
            t.RecordBlockServed("b2", 50);
            t.RecordBlockServed("unknown", 999);

            var drained = t.Drain();
            drained.Should().ContainSingle(i => i.FileId == "file-1" && i.Bytes == 150);
            t.Drain().Should().BeEmpty();
        }

        [Fact]
        public void Mapping_and_counts_survive_restart()
        {
            var t = new EgressTracker(_dir);
            t.MapBlock("b1", "file-1");
            t.RecordBlockServed("b1", 10);

            var reopened = new EgressTracker(_dir);
            reopened.RecordBlockServed("b1", 5);
            reopened.Drain().Single().Bytes.Should().Be(15);
        }
    }
}
