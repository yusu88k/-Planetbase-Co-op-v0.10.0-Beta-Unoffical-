using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace PlanetbaseCoop
{
    public sealed class CoopXmlDifference
    {
        public CoopXmlDifference(
            bool areEqual,
            string path,
            string leftValue,
            string rightValue,
            string description)
        {
            AreEqual = areEqual;
            Path = path ?? string.Empty;
            LeftValue = leftValue ?? string.Empty;
            RightValue = rightValue ?? string.Empty;
            Description = description ?? string.Empty;
        }

        public bool AreEqual { get; private set; }
        public string Path { get; private set; }
        public string LeftValue { get; private set; }
        public string RightValue { get; private set; }
        public string Description { get; private set; }

        internal static CoopXmlDifference Equal()
        {
            return new CoopXmlDifference(true, string.Empty, string.Empty, string.Empty, "XML documents are equal");
        }
    }

    /// <summary>
    /// Pure XML diagnostics shared by unit tests and the runtime boundary.
    /// Formatting, comments and attribute order do not affect the digest;
    /// element order remains significant because Planetbase serializes lists.
    /// </summary>
    public static class CoopDiagnostics
    {
        internal const int MaxXmlBytes = 8 * 1024 * 1024;
        internal const int MaxXmlDepth = 256;
        internal const int MaxXmlElements = 100000;
        internal const int MaxXmlAttributes = 250000;
        private static readonly Encoding Utf8 = new UTF8Encoding(false);

        public static string ComputeCanonicalXmlDigest(byte[] xmlBytes)
        {
            byte[] canonical = Canonicalize(xmlBytes);
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(canonical);
                var result = new StringBuilder(digest.Length * 2);
                for (int i = 0; i < digest.Length; i++) result.Append(digest[i].ToString("x2"));
                return result.ToString();
            }
        }

        public static CoopXmlDifference FindFirstDifference(byte[] leftXml, byte[] rightXml)
        {
            XmlDocument left = LoadXmlDocument(leftXml);
            XmlDocument right = LoadXmlDocument(rightXml);
            XmlElement leftRoot = left.DocumentElement;
            XmlElement rightRoot = right.DocumentElement;

            if (leftRoot == null || rightRoot == null)
            {
                if (leftRoot == null && rightRoot == null) return CoopXmlDifference.Equal();
                string path = leftRoot == null ? GetPath(rightRoot) : GetPath(leftRoot);
                return new CoopXmlDifference(
                    false,
                    path,
                    leftRoot == null ? string.Empty : leftRoot.Name,
                    rightRoot == null ? string.Empty : rightRoot.Name,
                    leftRoot == null ? "node missing on left" : "node missing on right");
            }

            CoopXmlDifference difference = CompareNodes(leftRoot, rightRoot);
            return difference ?? CoopXmlDifference.Equal();
        }

        internal static XmlDocument LoadXmlDocument(byte[] xmlBytes)
        {
            if (xmlBytes == null) throw new ArgumentNullException("xmlBytes");
            if (xmlBytes.Length > MaxXmlBytes)
                throw new XmlException("XML document exceeds the co-op snapshot size limit.");

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
                MaxCharactersInDocument = MaxXmlBytes,
                XmlResolver = null
            };
            var document = new XmlDocument { PreserveWhitespace = false, XmlResolver = null };
            using (var stream = new MemoryStream(xmlBytes, false))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                int elementCount = 0;
                int attributeCount = 0;
                while (reader.Read())
                {
                    if (reader.Depth > MaxXmlDepth)
                        throw new XmlException("XML document exceeds the co-op snapshot depth limit.");
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    elementCount++;
                    attributeCount = checked(attributeCount + reader.AttributeCount);
                    if (elementCount > MaxXmlElements)
                        throw new XmlException("XML document exceeds the co-op snapshot element limit.");
                    if (attributeCount > MaxXmlAttributes)
                        throw new XmlException("XML document exceeds the co-op snapshot attribute limit.");
                }
            }
            using (var stream = new MemoryStream(xmlBytes, false))
            using (XmlReader reader = XmlReader.Create(stream, settings))
            {
                document.Load(reader);
            }
            return document;
        }

        internal static string GetPath(XmlNode node)
        {
            if (node == null) return string.Empty;
            if (node.NodeType == XmlNodeType.Attribute)
            {
                var attribute = (XmlAttribute)node;
                return GetPath(attribute.OwnerElement) + "/@" + attribute.Name;
            }
            if (node.NodeType != XmlNodeType.Element) return GetPath(node.ParentNode);

            int index = 1;
            for (XmlNode sibling = node.PreviousSibling; sibling != null; sibling = sibling.PreviousSibling)
            {
                if (sibling.NodeType == XmlNodeType.Element &&
                    StringComparer.Ordinal.Equals(sibling.Name, node.Name))
                {
                    index++;
                }
            }

            string segment = "/" + node.Name + "[" + index + "]";
            return node.ParentNode == null || node.ParentNode.NodeType == XmlNodeType.Document
                ? segment
                : GetPath(node.ParentNode) + segment;
        }

        private static byte[] Canonicalize(byte[] xmlBytes)
        {
            XmlDocument document = LoadXmlDocument(xmlBytes);
            if (document.DocumentElement == null) return new byte[0];

            using (var stream = new MemoryStream())
            {
                var settings = new XmlWriterSettings
                {
                    Encoding = Utf8,
                    Indent = false,
                    NewLineHandling = NewLineHandling.None,
                    OmitXmlDeclaration = true
                };
                using (XmlWriter writer = XmlWriter.Create(stream, settings))
                {
                    WriteCanonicalNode(writer, document.DocumentElement);
                }
                return stream.ToArray();
            }
        }

        private static void WriteCanonicalNode(XmlWriter writer, XmlNode node)
        {
            if (node.NodeType == XmlNodeType.Element)
            {
                writer.WriteStartElement(node.Prefix, node.LocalName, node.NamespaceURI);
                foreach (XmlAttribute attribute in SortedAttributes(node))
                {
                    writer.WriteAttributeString(
                        attribute.Prefix,
                        attribute.LocalName,
                        attribute.NamespaceURI,
                        attribute.Value);
                }
                foreach (XmlNode child in SignificantChildren(node)) WriteCanonicalNode(writer, child);
                writer.WriteEndElement();
                return;
            }

            if (node.NodeType == XmlNodeType.Text || node.NodeType == XmlNodeType.CDATA)
                writer.WriteString(node.Value ?? string.Empty);
        }

        private static CoopXmlDifference CompareNodes(XmlNode left, XmlNode right)
        {
            string path = GetPath(left);
            if (left.NodeType != right.NodeType || !StringComparer.Ordinal.Equals(left.Name, right.Name))
            {
                return new CoopXmlDifference(false, path, left.Name, right.Name, "node name or type differs");
            }

            if (left.NodeType == XmlNodeType.Text || left.NodeType == XmlNodeType.CDATA)
            {
                if (!StringComparer.Ordinal.Equals(left.Value, right.Value))
                    return new CoopXmlDifference(false, path, left.Value, right.Value, "text value differs");
                return null;
            }

            List<XmlAttribute> leftAttributes = SortedAttributes(left);
            List<XmlAttribute> rightAttributes = SortedAttributes(right);
            int attributeCount = Math.Max(leftAttributes.Count, rightAttributes.Count);
            for (int i = 0; i < attributeCount; i++)
            {
                if (i >= leftAttributes.Count)
                {
                    XmlAttribute attribute = rightAttributes[i];
                    return new CoopXmlDifference(false, path + "/@" + attribute.Name,
                        string.Empty, attribute.Value, "attribute missing on left");
                }
                if (i >= rightAttributes.Count)
                {
                    XmlAttribute attribute = leftAttributes[i];
                    return new CoopXmlDifference(false, path + "/@" + attribute.Name,
                        attribute.Value, string.Empty, "attribute missing on right");
                }

                XmlAttribute leftAttribute = leftAttributes[i];
                XmlAttribute rightAttribute = rightAttributes[i];
                if (!StringComparer.Ordinal.Equals(leftAttribute.Name, rightAttribute.Name))
                {
                    return new CoopXmlDifference(false, path + "/@" + leftAttribute.Name,
                        leftAttribute.Name, rightAttribute.Name, "attribute name differs");
                }
                if (!StringComparer.Ordinal.Equals(leftAttribute.Value, rightAttribute.Value))
                {
                    return new CoopXmlDifference(false, path + "/@" + leftAttribute.Name,
                        leftAttribute.Value, rightAttribute.Value, "attribute value differs");
                }
            }

            List<XmlNode> leftChildren = SignificantChildren(left);
            List<XmlNode> rightChildren = SignificantChildren(right);
            int childCount = Math.Max(leftChildren.Count, rightChildren.Count);
            for (int i = 0; i < childCount; i++)
            {
                if (i >= leftChildren.Count)
                {
                    XmlNode child = rightChildren[i];
                    return new CoopXmlDifference(false, GetPath(child), string.Empty,
                        NodeValue(child), "node missing on left");
                }
                if (i >= rightChildren.Count)
                {
                    XmlNode child = leftChildren[i];
                    return new CoopXmlDifference(false, GetPath(child), NodeValue(child),
                        string.Empty, "node missing on right");
                }

                CoopXmlDifference childDifference = CompareNodes(leftChildren[i], rightChildren[i]);
                if (childDifference != null) return childDifference;
            }
            return null;
        }

        private static List<XmlAttribute> SortedAttributes(XmlNode node)
        {
            var attributes = new List<XmlAttribute>();
            if (node.Attributes != null)
            {
                foreach (XmlAttribute attribute in node.Attributes) attributes.Add(attribute);
            }
            attributes.Sort(delegate(XmlAttribute left, XmlAttribute right)
            {
                return StringComparer.Ordinal.Compare(left.Name, right.Name);
            });
            return attributes;
        }

        private static List<XmlNode> SignificantChildren(XmlNode node)
        {
            var children = new List<XmlNode>();
            foreach (XmlNode child in node.ChildNodes)
            {
                if (child.NodeType == XmlNodeType.Element || child.NodeType == XmlNodeType.CDATA ||
                    (child.NodeType == XmlNodeType.Text && !string.IsNullOrWhiteSpace(child.Value)))
                {
                    children.Add(child);
                }
            }
            return children;
        }

        private static string NodeValue(XmlNode node)
        {
            if (node == null) return string.Empty;
            return node.NodeType == XmlNodeType.Element ? node.Name : node.Value ?? string.Empty;
        }
    }
}
