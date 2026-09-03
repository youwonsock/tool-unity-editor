using System;

namespace Common.TransformPath
{
    /// <summary>Provider capability and route descriptor helpers.</summary>
    internal static class PathProviderUtility
    {
        public static bool TryValidateReady(IPathProvider provider, out string error)
        {
            if (provider == null)
            {
                error = "Path provider is required.";
                return false;
            }

            if (!provider.IsInitialized || !provider.IsReady)
            {
                error = "Path provider must be initialized and ready.";
                return false;
            }

            error = null;
            return true;
        }

        public static bool TryGetRouteSegmentCount(
            IPathProvider provider,
            out int count,
            out string error)
        {
            count = 0;
            if (!TryValidateReady(provider, out error))
                return false;

            if (provider is IPathSequenceProvider sequenceProvider)
            {
                count = sequenceProvider.SegmentCount;
                if (count <= 0)
                {
                    error = "A path sequence requires at least one segment.";
                    return false;
                }

                return true;
            }

            if (provider is IPathMovementProvider)
            {
                count = 1;
                return true;
            }

            error = "A path provider must expose movement settings or sequence segments.";
            return false;
        }

        public static bool TryGetDescriptor(
            IPathProvider provider,
            int index,
            out PathSegmentDescriptor descriptor,
            out string error)
        {
            descriptor = default(PathSegmentDescriptor);
            if (!TryGetRouteSegmentCount(provider, out int count, out error))
                return false;
            if (index < 0 || index >= count)
            {
                error = $"Route segment index {index} is outside 0..{count - 1}.";
                return false;
            }

            if (provider is IPathSequenceProvider sequenceProvider)
            {
                descriptor = sequenceProvider.GetSegment(index);
                if (!TryValidateDescriptor(descriptor, index, out error))
                    return false;
                return true;
            }

            IPathMovementProvider movementProvider = provider as IPathMovementProvider;
            descriptor = new PathSegmentDescriptor(
                movementProvider,
                movementProvider.MovementSettings,
                false);
            return TryValidateDescriptor(descriptor, index, out error);
        }

        public static bool AreSameDescriptor(
            PathSegmentDescriptor left,
            PathSegmentDescriptor right)
        {
            return ReferenceEquals(left.Provider, right.Provider)
                && left.PreservePreviousSpeed == right.PreservePreviousSpeed
                && PathMovementSettingsUtility.AreSame(
                    left.MovementSettings,
                    right.MovementSettings);
        }

        private static bool TryValidateDescriptor(
            PathSegmentDescriptor descriptor,
            int index,
            out string error)
        {
            if (!TryValidateReady(descriptor.Provider, out error))
            {
                error = $"Route segment {index} provider is not ready: {error}";
                return false;
            }

            if (!PathMovementSettingsUtility.TryValidate(
                    descriptor.MovementSettings,
                    out error))
            {
                error = $"Route segment {index} movement settings are invalid: {error}";
                return false;
            }

            error = null;
            return true;
        }
    }
}
