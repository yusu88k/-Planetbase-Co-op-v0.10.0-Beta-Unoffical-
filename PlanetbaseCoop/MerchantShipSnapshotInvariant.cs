using System;
using System.Globalization;
using System.Xml;

namespace PlanetbaseCoop
{
    public sealed class MerchantShipSnapshotValidationResult
    {
        internal MerchantShipSnapshotValidationResult(
            bool isValid,
            string code,
            string message,
            string path,
            int merchantShipId,
            string missingNodeName,
            int merchantShipCount)
        {
            IsValid = isValid;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
            Path = path ?? string.Empty;
            MerchantShipId = merchantShipId;
            MissingNodeName = missingNodeName ?? string.Empty;
            MerchantShipCount = merchantShipCount;
        }

        public bool IsValid { get; private set; }
        public string Code { get; private set; }
        public string Message { get; private set; }
        public string Path { get; private set; }
        public int MerchantShipId { get; private set; }
        public string MissingNodeName { get; private set; }
        public int MerchantShipCount { get; private set; }
    }

    /// <summary>
    /// Validates the serialized MerchantShip state before Planetbase can enter
    /// its update loop. Trading ships dereference all three product collections.
    /// </summary>
    public static class MerchantShipSnapshotInvariant
    {
        private static readonly string[] TradingCollections =
            { "needed-products", "contained-products", "provided-products" };

        public static MerchantShipSnapshotValidationResult Validate(byte[] xmlBytes)
        {
            if (xmlBytes == null || xmlBytes.Length == 0)
                return Invalid("empty-snapshot", "Snapshot XML is empty.", string.Empty, -1, string.Empty, 0);

            try
            {
                return Validate(CoopDiagnostics.LoadXmlDocument(xmlBytes));
            }
            catch (XmlException ex)
            {
                return Invalid("malformed-xml", "Snapshot XML is malformed: " + ex.Message,
                    string.Empty, -1, string.Empty, 0);
            }
        }

        public static MerchantShipSnapshotValidationResult Validate(XmlDocument document)
        {
            if (document == null)
                return Invalid("empty-document", "Snapshot XML document is null.", string.Empty, -1, string.Empty, 0);

            XmlNodeList merchants = document.SelectNodes("//ship[@type='MerchantShip']");
            int merchantCount = merchants == null ? 0 : merchants.Count;
            if (merchants == null)
                return Valid(0);

            foreach (XmlNode merchant in merchants)
            {
                int merchantId = ReadIntValue(merchant["id"], -1);
                XmlNode tradeStateNode = merchant["trade-state"];
                int tradeState;
                if (!TryReadIntValue(tradeStateNode, out tradeState))
                {
                    string tradePath = CoopDiagnostics.GetPath(merchant) + "/trade-state[1]";
                    return Invalid(
                        "invalid-trade-state",
                        "MerchantShip " + DisplayId(merchantId) + " has no valid trade-state value.",
                        tradePath,
                        merchantId,
                        "trade-state",
                        merchantCount);
                }

                if (tradeState < 0 || tradeState > 2)
                {
                    string tradePath = CoopDiagnostics.GetPath(tradeStateNode) + "/@value";
                    return Invalid(
                        "invalid-trade-state",
                        "MerchantShip " + DisplayId(merchantId) + " has an out-of-range trade-state value.",
                        tradePath,
                        merchantId,
                        "trade-state",
                        merchantCount);
                }

                if (tradeState != 1) continue;
                int presentCollectionCount = 0;
                for (int i = 0; i < TradingCollections.Length; i++)
                {
                    if (merchant[TradingCollections[i]] != null) presentCollectionCount++;
                }

                if (presentCollectionCount == 0)
                {
                    string transientPath = CoopDiagnostics.GetPath(merchant) +
                        "/" + TradingCollections[0] + "[1]";
                    return Invalid(
                        "uncommitted-trading-state",
                        "MerchantShip " + DisplayId(merchantId) +
                            " is in the vanilla pre-agreement trading state.",
                        transientPath,
                        merchantId,
                        TradingCollections[0],
                        merchantCount);
                }

                for (int i = 0; i < TradingCollections.Length; i++)
                {
                    string requiredNode = TradingCollections[i];
                    if (merchant[requiredNode] != null) continue;

                    string requiredPath = CoopDiagnostics.GetPath(merchant) + "/" + requiredNode + "[1]";
                    return Invalid(
                        "missing-trading-collection",
                        "MerchantShip " + DisplayId(merchantId) + " is trading but required XML node '" +
                            requiredNode + "' is missing.",
                        requiredPath,
                        merchantId,
                        requiredNode,
                        merchantCount);
                }
            }

            return Valid(merchantCount);
        }

        private static MerchantShipSnapshotValidationResult Valid(int merchantCount)
        {
            return new MerchantShipSnapshotValidationResult(
                true, "valid", "MerchantShip snapshot invariants are valid.",
                string.Empty, -1, string.Empty, merchantCount);
        }

        private static MerchantShipSnapshotValidationResult Invalid(
            string code,
            string message,
            string path,
            int merchantShipId,
            string missingNodeName,
            int merchantCount)
        {
            return new MerchantShipSnapshotValidationResult(
                false, code, message, path, merchantShipId, missingNodeName, merchantCount);
        }

        private static int ReadIntValue(XmlNode node, int fallback)
        {
            int value;
            return TryReadIntValue(node, out value) ? value : fallback;
        }

        private static bool TryReadIntValue(XmlNode node, out int value)
        {
            value = 0;
            if (node == null || node.Attributes == null) return false;
            XmlAttribute attribute = node.Attributes["value"];
            return attribute != null && int.TryParse(
                attribute.Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static string DisplayId(int merchantShipId)
        {
            return merchantShipId >= 0
                ? merchantShipId.ToString(CultureInfo.InvariantCulture)
                : "<unknown>";
        }
    }
}
