using System;
using System.Collections;
using System.Collections.Generic;
using Reflection = System.Reflection;
using Planetbase;
using UnityEngine;

namespace PlanetbaseCoop
{
    internal static class CoopObjectBridge
    {
        private const Reflection.BindingFlags StaticFields = Reflection.BindingFlags.Static | Reflection.BindingFlags.Public | Reflection.BindingFlags.NonPublic;
        private static readonly Reflection.FieldInfo ConstructionsField = typeof(Construction).GetField("mConstructions", StaticFields);
        private static readonly Reflection.FieldInfo ComponentsField = typeof(ConstructionComponent).GetField("mComponents", StaticFields);
        private static readonly Reflection.FieldInfo CharactersField = typeof(Character).GetField("mCharacters", StaticFields);
        private static readonly Reflection.FieldInfo ResourcesField = typeof(Resource).GetField("mResources", StaticFields);
        private static readonly Reflection.FieldInfo ShipsField = typeof(Ship).GetField("mShips", StaticFields);

        public static CoopObjectReference Capture(Selectable selectable, bool includePosition)
        {
            if (selectable == null) return null;
            Vector3 position = includePosition ? selectable.getPosition() : Vector3.zero;
            return new CoopObjectReference(
                selectable.getId(),
                selectable.GetType().Name,
                position.x,
                position.y,
                position.z,
                includePosition);
        }

        public static Module ResolveModule(
            CoopObjectReference reference,
            out CoopObjectResolution resolution)
        {
            Selectable target;
            if (!TryResolve(reference, ReadSelectables(ConstructionsField), out target, out resolution))
                return null;
            return target as Module;
        }

        public static Selectable ResolveSelectable(
            CoopObjectReference reference,
            out CoopObjectResolution resolution)
        {
            List<Selectable> candidates = new List<Selectable>();
            AddRange(candidates, ReadSelectables(ConstructionsField));
            AddRange(candidates, ReadSelectables(ComponentsField));
            AddRange(candidates, ReadSelectables(CharactersField));
            AddRange(candidates, ReadSelectables(ResourcesField));
            AddRange(candidates, ReadSelectables(ShipsField));
            Selectable target;
            return TryResolve(reference, candidates, out target, out resolution) ? target : null;
        }

        private static bool TryResolve(
            CoopObjectReference reference,
            IList<Selectable> selectables,
            out Selectable target,
            out CoopObjectResolution resolution)
        {
            target = null;
            List<CoopObjectCandidate> candidates = new List<CoopObjectCandidate>(selectables.Count);
            for (int i = 0; i < selectables.Count; i++)
            {
                Selectable selectable = selectables[i];
                if (selectable == null) continue;
                Vector3 position = selectable.getPosition();
                candidates.Add(new CoopObjectCandidate(
                    selectable.getId(),
                    selectable.GetType().Name,
                    position.x,
                    position.y,
                    position.z));
            }

            CoopObjectCandidate match;
            if (!CoopObjectResolver.TryResolve(reference, candidates, out match, out resolution))
                return false;

            for (int i = 0; i < selectables.Count; i++)
            {
                Selectable selectable = selectables[i];
                if (selectable == null || selectable.getId() != match.ObjectId ||
                    !StringComparer.Ordinal.Equals(selectable.GetType().Name, match.TypeName)) continue;
                Vector3 position = selectable.getPosition();
                if (position.x == match.PositionX && position.y == match.PositionY && position.z == match.PositionZ)
                {
                    target = selectable;
                    return true;
                }
            }
            resolution = CoopObjectResolution.Missing;
            return false;
        }

        private static List<Selectable> ReadSelectables(Reflection.FieldInfo field)
        {
            List<Selectable> result = new List<Selectable>();
            if (field == null) return result;
            try
            {
                IEnumerable values = field.GetValue(null) as IEnumerable;
                if (values == null) return result;
                foreach (object value in values)
                {
                    Selectable selectable = value as Selectable;
                    if (selectable != null) result.Add(selectable);
                }
            }
            catch (Exception ex)
            {
                CoopPlugin.Log.LogWarning("[Coop] nesne listesi okunamadi: " + ex.Message);
            }
            return result;
        }

        private static void AddRange(List<Selectable> destination, IList<Selectable> source)
        {
            for (int i = 0; i < source.Count; i++) destination.Add(source[i]);
        }
    }
}
