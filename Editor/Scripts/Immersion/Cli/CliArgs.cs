using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Immersion.Export
{
	/// <summary>Command-line helpers shared by the batch-mode entry points (<c>-executeMethod</c>).</summary>
	public static class CliArgs
	{
		/// <summary>Value following <paramref name="flag"/> on the Unity command line, or null.</summary>
		public static string Get(string flag)
		{
			var args = Environment.GetCommandLineArgs();
			for (var i = 0; i < args.Length - 1; i++)
				if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase))
					return args[i + 1];
			return null;
		}

		/// <summary>Splits a semicolon-separated list; empty segments are kept (they align lists by index).</summary>
		public static List<string> Split(string value)
		{
			return string.IsNullOrEmpty(value)
				? new List<string>()
				: value.Split(';').Select(s => s.Trim().Trim('"')).ToList();
		}

		/// <summary>Exit with <paramref name="code"/> in batch mode; no-op in an interactive editor.</summary>
		public static void Exit(int code)
		{
			if (Application.isBatchMode) EditorApplication.Exit(code);
		}
	}
}
