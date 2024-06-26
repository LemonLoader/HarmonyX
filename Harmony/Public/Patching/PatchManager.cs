using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HarmonyLib.Public.Patching
{
	/// <summary>
	/// A global manager for handling Harmony patch state. Contains information about all patched methods and all
	/// actual <see cref="MethodPatcher"/> instances that handle patching implementation.
	/// </summary>
	///
	public static class PatchManager
	{
		private static readonly Dictionary<MethodBase, PatchInfo> PatchInfos = new();
		private static readonly Dictionary<MethodBase, MethodPatcher> MethodPatchers = new();

		private static readonly ConditionalWeakTable<MethodBase, MethodBase> ReplacementToOriginals = new();
		private static readonly Dictionary<long, MethodBase[]> ReplacementToOriginalsMono = new();

		private static readonly AccessTools.FieldRef<StackFrame, long> MethodAddressRef;

		static PatchManager()
		{
			// this field is used to find methods from stackframes in Mono
			if (AccessTools.IsMonoRuntime && AccessTools.Field(typeof(StackFrame), "methodAddress") is FieldInfo field)
				MethodAddressRef = AccessTools.FieldRefAccess<StackFrame, long>(field);

			ResolvePatcher += ManagedMethodPatcher.TryResolve;
			ResolvePatcher += NativeDetourMethodPatcher.TryResolve;
		}

		/// <summary>
		/// Method patcher resolve event.
		/// </summary>
		/// <remarks>
		/// When a method is to be patched, this resolver event is called once on the method to determine which
		/// <see cref="MethodPatcher"/> backend to use in order to patch the method.
		/// To make Harmony use the specified backend, set <see cref="PatcherResolverEventArgs.MethodPatcher"/> to an
		/// instance of the method patcher backend to use.
		/// </remarks>
		///
		public static event EventHandler<PatcherResolverEventArgs> ResolvePatcher;

		/// <summary>
		/// Creates or gets an existing instance of <see cref="MethodPatcher"/> that handles patching the method.
		/// </summary>
		/// <param name="methodBase">Method to patch.</param>
		/// <returns>Instance of <see cref="MethodPatcher"/> that handles patching the method.</returns>
		/// <exception cref="NullReferenceException">No suitable patcher found for the method.</exception>
		///
		public static MethodPatcher GetMethodPatcher(this MethodBase methodBase)
		{
			lock (MethodPatchers)
			{
				if (MethodPatchers.TryGetValue(methodBase, out var methodPatcher))
					return methodPatcher;
				var args = new PatcherResolverEventArgs {Original = methodBase};
				ResolvePatcher?.Invoke(null, args);
				if (args.MethodPatcher == null)
					throw new NullReferenceException($"No suitable patcher found for {methodBase.FullDescription()}");
				return MethodPatchers[methodBase] = args.MethodPatcher;
			}
		}

		/// <summary>
		/// Gets patch info for the given target method.
		/// </summary>
		/// <param name="methodBase">Method to get patch info for.</param>
		/// <returns>Current patch info of the method.</returns>
		///
		public static PatchInfo GetPatchInfo(this MethodBase methodBase)
		{
			lock (PatchInfos)
				return PatchInfos.GetValueSafe(methodBase);
		}

		/// <summary>
		/// Gets or creates patch info for the given method.
		/// </summary>
		/// <param name="methodBase">Method to get info from.</param>
		/// <returns>An existing or new patch info for the method containing information about the applied patches.</returns>
		///
		public static PatchInfo ToPatchInfo(this MethodBase methodBase)
		{
			lock (PatchInfos)
			{
				if (PatchInfos.TryGetValue(methodBase, out var info))
					return info;

				return PatchInfos[methodBase] = new PatchInfo();
			}
		}

		/// <summary>
		/// Gets all methods that have been patched.
		/// </summary>
		/// <returns>List of methods that have been patched.</returns>
		///
		public static IEnumerable<MethodBase> GetPatchedMethods()
		{
			lock (PatchInfos)
				return PatchInfos.Keys.ToArray();
		}

		// With mono, useReplacement is used to either return the original or the replacement
		// On .NET, useReplacement is ignored and the original is always returned
		internal static MethodBase GetRealMethod(MethodInfo method, bool useReplacement)
		{
			var identifiableMethod = method.Identifiable();
			lock (ReplacementToOriginals)
				if (ReplacementToOriginals.TryGetValue(identifiableMethod, out var original))
					return original;

			if (AccessTools.IsMonoRuntime)
			{
				var methodAddress = (long)method.MethodHandle.GetFunctionPointer();
				lock (ReplacementToOriginalsMono)
					if (ReplacementToOriginalsMono.TryGetValue(methodAddress, out var info))
						return useReplacement ? info[1] : info[0];
			}

			return method;
		}

		internal static MethodBase GetStackFrameMethod(StackFrame frame, bool useReplacement)
		{
			if (frame.GetMethod() is MethodInfo method)
				return GetRealMethod(method, useReplacement);

			if (MethodAddressRef != null)
			{
				var methodAddress = MethodAddressRef(frame);
				lock (ReplacementToOriginalsMono)
					if (ReplacementToOriginalsMono.TryGetValue(methodAddress, out var info))
						return useReplacement ? info[1] : info[0];
			}

			return null;
		}

		internal static void AddReplacementOriginal(MethodBase original, MethodBase replacement)
		{
			replacement = (replacement as MethodInfo)?.Identifiable() ?? replacement;
			if (replacement == null)
				return;

			lock (ReplacementToOriginals)
			{
				if(!ReplacementToOriginals.TryAdd(replacement, original))
				{
					ReplacementToOriginals.Remove(replacement);
					ReplacementToOriginals.Add(replacement, original);
				}
			}

			if (AccessTools.IsMonoRuntime)
			{
				var methodAddress = (long)replacement.MethodHandle.GetFunctionPointer();
				lock (ReplacementToOriginalsMono)
					ReplacementToOriginalsMono[methodAddress] = [original, replacement];
			}
		}

		/// <summary>
		/// Removes all method resolvers. Use with care, this removes the default ones too!
		/// </summary>
		public static void ClearAllPatcherResolvers()
		{
			ResolvePatcher = null;
		}

		/// <summary>
		/// Patcher resolve event arguments.
		/// </summary>
		///
		public class PatcherResolverEventArgs : EventArgs
		{
			/// <summary>
			/// Original method that is to be patched.
			/// </summary>
			///
			public MethodBase Original { get; internal set; }

			/// <summary>
			/// Method patcher to use to patch <see cref="Original"/>.
			/// Set this value to specify which one to use.
			/// </summary>
			///
			public MethodPatcher MethodPatcher { get; set; }
		}
	}
}
