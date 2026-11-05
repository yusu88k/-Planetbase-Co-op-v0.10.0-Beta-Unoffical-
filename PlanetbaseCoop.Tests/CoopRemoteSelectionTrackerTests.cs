using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopRemoteSelectionTrackerTests
    {
        [Fact]
        public void NewRemoteSelectionIsStoredAsVisualStateOnly()
        {
            var tracker = new CoopRemoteSelectionTracker();
            var selection = new CoopSelectionData
            {
                Active = true,
                Sequence = 4,
                PrimaryReference = new CoopObjectReference(7, "Colonist", 10f, 0f, 20f, true)
            };

            Assert.True(tracker.TryAccept(selection));
            Assert.True(tracker.Current.Active);
            Assert.Equal(7, tracker.Current.PrimaryReference.ObjectId);
            Assert.Equal(4, tracker.LastSequence);
        }

        [Fact]
        public void DuplicateOrOlderRemoteSelectionIsIgnored()
        {
            var tracker = new CoopRemoteSelectionTracker();
            Assert.True(tracker.TryAccept(new CoopSelectionData { Active = false, Sequence = 5 }));

            Assert.False(tracker.TryAccept(new CoopSelectionData { Active = false, Sequence = 5 }));
            Assert.False(tracker.TryAccept(new CoopSelectionData { Active = false, Sequence = 4 }));
            Assert.Equal(5, tracker.LastSequence);
        }

        [Fact]
        public void InactiveSelectionClearsOnlyRemoteVisualState()
        {
            var tracker = new CoopRemoteSelectionTracker();
            Assert.True(tracker.TryAccept(new CoopSelectionData
            {
                Active = true,
                Sequence = 1,
                PrimaryReference = new CoopObjectReference(7, "Colonist", 10f, 0f, 20f, true)
            }));

            Assert.True(tracker.TryAccept(new CoopSelectionData { Active = false, Sequence = 2 }));
            Assert.False(tracker.Current.Active);
            Assert.Null(tracker.Current.PrimaryReference);
        }

        [Fact]
        public void ReturnedSelectionIsDefensiveCopy()
        {
            var tracker = new CoopRemoteSelectionTracker();
            Assert.True(tracker.TryAccept(new CoopSelectionData
            {
                Active = true,
                Sequence = 1,
                PrimaryReference = new CoopObjectReference(7, "Colonist", 10f, 0f, 20f, true)
            }));

            CoopSelectionData first = tracker.Current;
            first.Active = false;

            Assert.True(tracker.Current.Active);
        }
    }
}
