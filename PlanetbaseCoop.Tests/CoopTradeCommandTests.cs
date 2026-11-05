using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopTradeCommandTests
    {
        [Fact]
        public void AgreementRoundTripsShipAndBothProductLists()
        {
            var data = new CoopTradeCommandData(
                CoopTradeAction.Agree,
                new CoopObjectReference(44, "MerchantShip", 10f, 0f, 20f, true),
                new[]
                {
                    new CoopTradeProductData(CoopTradeProductKind.Resource, "ResourceTypeMetal", 5)
                },
                new[]
                {
                    new CoopTradeProductData(CoopTradeProductKind.Tech, "TechDrillerBot", 1),
                    new CoopTradeProductData(CoopTradeProductKind.Bot, "SpecializationCarrierBot", 2)
                });

            byte[] payload = CoopTradeCommandCodec.Encode(data);

            Assert.True(CoopTradeCommandCodec.TryDecode(payload, out CoopTradeCommandData actual));
            Assert.Equal(CoopTradeAction.Agree, actual.Action);
            Assert.Equal(44, actual.ShipReference.ObjectId);
            Assert.Single(actual.NeededProducts);
            Assert.Equal(2, actual.ProvidedProducts.Length);
            Assert.Equal("TechDrillerBot", actual.ProvidedProducts[0].ProductId);
        }

        [Theory]
        [InlineData(CoopTradeAction.Start)]
        [InlineData(CoopTradeAction.Cancel)]
        [InlineData(CoopTradeAction.Dismiss)]
        public void LifecycleActionRoundTripsWithoutProducts(CoopTradeAction action)
        {
            var data = new CoopTradeCommandData(
                action,
                new CoopObjectReference(45, "MerchantShip", 1f, 0f, 2f, true),
                new CoopTradeProductData[0],
                new CoopTradeProductData[0]);

            byte[] payload = CoopTradeCommandCodec.Encode(data);

            Assert.True(CoopTradeCommandCodec.TryDecode(payload, out CoopTradeCommandData actual));
            Assert.Equal(action, actual.Action);
            Assert.Empty(actual.NeededProducts);
            Assert.Empty(actual.ProvidedProducts);
        }

        [Fact]
        public void RejectsProductsOnLifecycleActionsAndDuplicateOrUnsafeEntries()
        {
            CoopObjectReference ship = new CoopObjectReference(
                45, "MerchantShip", 1f, 0f, 2f, true);
            CoopTradeProductData metal = new CoopTradeProductData(
                CoopTradeProductKind.Resource, "ResourceTypeMetal", 1);
            Assert.Throws<System.ArgumentException>(() => CoopTradeCommandCodec.Encode(
                new CoopTradeCommandData(
                    CoopTradeAction.Start, ship, new[] { metal }, new CoopTradeProductData[0])));
            Assert.Throws<System.ArgumentException>(() => CoopTradeCommandCodec.Encode(
                new CoopTradeCommandData(
                    CoopTradeAction.Agree, ship, new[] { metal, metal }, new[] { metal })));
            Assert.Throws<System.ArgumentException>(() => CoopTradeCommandCodec.Encode(
                new CoopTradeCommandData(
                    CoopTradeAction.Agree,
                    ship,
                    new[] { new CoopTradeProductData(CoopTradeProductKind.Resource, "../Metal", 1) },
                    new[] { metal })));
        }

        [Fact]
        public void RejectsEveryTruncationAndTrailingByte()
        {
            byte[] payload = CoopTradeCommandCodec.Encode(new CoopTradeCommandData(
                CoopTradeAction.Agree,
                new CoopObjectReference(44, "MerchantShip", 10f, 0f, 20f, true),
                new[] { new CoopTradeProductData(CoopTradeProductKind.Resource, "Metal", 2) },
                new[] { new CoopTradeProductData(CoopTradeProductKind.Resource, "Meal", 1) }));
            for (int length = 0; length < payload.Length; length++)
            {
                var truncated = new byte[length];
                System.Buffer.BlockCopy(payload, 0, truncated, 0, length);
                Assert.False(CoopTradeCommandCodec.TryDecode(truncated, out _));
            }
            System.Array.Resize(ref payload, payload.Length + 1);
            Assert.False(CoopTradeCommandCodec.TryDecode(payload, out _));
        }
    }
}
