using System;
using System.Xml;
using Xunit;

namespace PlanetbaseCoop.Tests
{
    public sealed class CoopEnvironmentStateTests
    {
        private static XmlElement BuildRoot()
        {
            XmlDocument document = new XmlDocument();
            XmlElement root = document.CreateElement("save-game");
            document.AppendChild(root);
            return root;
        }

        [Fact]
        public void RoundTripsWindStateThroughXml()
        {
            XmlElement root = BuildRoot();
            var state = new CoopEnvironmentWindState
            {
                TimeToWindChange = 7.25f,
                WindOffset = -0.0125f,
                WindDirectionX = 0.7071f,
                WindDirectionY = 0.125f,
                WindDirectionZ = -0.7071f
            };

            CoopEnvironmentState.Write(root, state);
            Assert.True(CoopEnvironmentState.TryRead(root, out CoopEnvironmentWindState read));
            Assert.Equal(state.TimeToWindChange, read.TimeToWindChange);
            Assert.Equal(state.WindOffset, read.WindOffset);
            Assert.Equal(state.WindDirectionX, read.WindDirectionX);
            Assert.Equal(state.WindDirectionY, read.WindDirectionY);
            Assert.Equal(state.WindDirectionZ, read.WindDirectionZ);
        }

        [Fact]
        public void WriteReplacesExistingNodesSoRootStaysUnique()
        {
            XmlElement root = BuildRoot();
            var first = new CoopEnvironmentWindState
            {
                TimeToWindChange = 1f,
                WindOffset = 0f,
                WindDirectionX = 1f,
                WindDirectionY = 0f,
                WindDirectionZ = 0f
            };
            var second = new CoopEnvironmentWindState
            {
                TimeToWindChange = 2f,
                WindOffset = 0.01f,
                WindDirectionX = 0f,
                WindDirectionY = 1f,
                WindDirectionZ = 0f
            };

            CoopEnvironmentState.Write(root, first);
            CoopEnvironmentState.Write(root, second);

            Assert.True(CoopEnvironmentState.TryRead(root, out CoopEnvironmentWindState read));
            Assert.Equal(2f, read.TimeToWindChange);
        }

        [Fact]
        public void RejectsNonFiniteValues()
        {
            XmlElement root = BuildRoot();
            var invalid = new CoopEnvironmentWindState
            {
                TimeToWindChange = float.NaN,
                WindOffset = 0f,
                WindDirectionX = 0f,
                WindDirectionY = 0f,
                WindDirectionZ = 1f
            };
            Assert.Throws<ArgumentOutOfRangeException>(
                () => CoopEnvironmentState.Write(root, invalid));
        }

        [Fact]
        public void ReadFailsWhenNodeMissingOrDuplicated()
        {
            XmlElement root = BuildRoot();
            Assert.False(CoopEnvironmentState.TryRead(root, out _));

            var state = new CoopEnvironmentWindState
            {
                TimeToWindChange = 3f,
                WindOffset = 0f,
                WindDirectionX = 0f,
                WindDirectionY = 0f,
                WindDirectionZ = 1f
            };
            CoopEnvironmentState.Write(root, state);
            XmlNode original = root.SelectNodes(CoopEnvironmentState.ElementName)[0];
            root.AppendChild(original.Clone());

            Assert.False(CoopEnvironmentState.TryRead(root, out _));
        }
    }
}
