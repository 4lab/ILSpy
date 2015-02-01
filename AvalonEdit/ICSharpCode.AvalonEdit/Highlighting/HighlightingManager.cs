﻿// Copyright (c) AlphaSierraPapa for the SharpDevelop Team (for details please see \doc\copyright.txt)
// This code is distributed under the GNU LGPL (for details please see \doc\license.txt)

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Xml;

using ICSharpCode.AvalonEdit.Utils;

namespace ICSharpCode.AvalonEdit.Highlighting
{
	/// <summary>
	/// Manages a list of syntax highlighting definitions.
	/// </summary>
	/// <remarks>
	/// All memers on this class (including instance members) are thread-safe.
	/// </remarks>
	public class HighlightingManager : IHighlightingDefinitionReferenceResolver
	{
		sealed class DelayLoadedHighlightingDefinition : IHighlightingDefinition
		{
			readonly object lockObj = new object();
			readonly string name;
			Func<IHighlightingDefinition> lazyLoadingFunction;
			IHighlightingDefinition definition;
			Exception storedException;
			
			public DelayLoadedHighlightingDefinition(string name, Func<IHighlightingDefinition> lazyLoadingFunction)
			{
				this.name = name;
				this.lazyLoadingFunction = lazyLoadingFunction;
			}
			
			public string Name {
				get {
					if (name != null)
						return name;
					else
						return GetDefinition().Name;
				}
			}
			
			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Design", "CA1031:DoNotCatchGeneralExceptionTypes",
			                                                 Justification = "The exception will be rethrown")]
			IHighlightingDefinition GetDefinition()
			{
				Func<IHighlightingDefinition> func;
				lock (lockObj) {
					if (this.definition != null)
						return this.definition;
					func = this.lazyLoadingFunction;
				}
				Exception exception = null;
				IHighlightingDefinition def = null;
				try {
					using (var busyLock = BusyManager.Enter(this)) {
						if (!busyLock.Success)
							throw new InvalidOperationException("Tried to create delay-loaded highlighting definition recursively. Make sure the are no cyclic references between the highlighting definitions.");
						def = func();
					}
					if (def == null)
						throw new InvalidOperationException("Function for delay-loading highlighting definition returned null");
				} catch (Exception ex) {
					exception = ex;
				}
				lock (lockObj) {
					this.lazyLoadingFunction = null;
					if (this.definition == null && this.storedException == null) {
						this.definition = def;
						this.storedException = exception;
					}
					if (this.storedException != null)
						throw new HighlightingDefinitionInvalidException("Error delay-loading highlighting definition", this.storedException);
					return this.definition;
				}
			}
			
			public HighlightingRuleSet MainRuleSet {
				get {
					return GetDefinition().MainRuleSet;
				}
			}
			
			public HighlightingRuleSet GetNamedRuleSet(string name)
			{
				return GetDefinition().GetNamedRuleSet(name);
			}
			
			public HighlightingColor GetNamedColor(string name)
			{
				return GetDefinition().GetNamedColor(name);
			}
			
			public IEnumerable<HighlightingColor> NamedHighlightingColors {
				get {
					return GetDefinition().NamedHighlightingColors;
				}
			}
			
			public override string ToString()
			{
				return this.Name;
			}
		}
		
		readonly object lockObj = new object();
		Dictionary<string, IHighlightingDefinition> highlightingsByName = new Dictionary<string, IHighlightingDefinition>();
		Dictionary<string, IHighlightingDefinition> highlightingsByExtension = new Dictionary<string, IHighlightingDefinition>(StringComparer.OrdinalIgnoreCase);
		List<IHighlightingDefinition> allHighlightings = new List<IHighlightingDefinition>();
		
		/// <summary>
		/// Gets a highlighting definition by name.
		/// Returns null if the definition is not found.
		/// </summary>
		public IHighlightingDefinition GetDefinition(string name)
		{
			lock (lockObj) {
				IHighlightingDefinition rh;
				if (highlightingsByName.TryGetValue(name, out rh))
					return rh;
				else
					return null;
			}
		}
		
		/// <summary>
		/// Gets a copy of all highlightings.
		/// </summary>
		public ReadOnlyCollection<IHighlightingDefinition> HighlightingDefinitions {
			get {
				lock (lockObj) {
					return Array.AsReadOnly(allHighlightings.ToArray());
				}
			}
		}
		
		/// <summary>
		/// Gets the names of the registered highlightings.
		/// </summary>
		[ObsoleteAttribute("Use the HighlightingDefinitions property instead.")]
		public IEnumerable<string> HighlightingNames {
			get {
				lock (lockObj) {
					return new List<string>(highlightingsByName.Keys);
				}
			}
		}
		
		/// <summary>
		/// Gets a highlighting definition by extension.
		/// Returns null if the definition is not found.
		/// </summary>
		public IHighlightingDefinition GetDefinitionByExtension(string extension)
		{
			lock (lockObj) {
				IHighlightingDefinition rh;
				if (highlightingsByExtension.TryGetValue(extension, out rh))
					return rh;
				else
					return null;
			}
		}
		
		/// <summary>
		/// Registers a highlighting definition.
		/// </summary>
		/// <param name="name">The name to register the definition with.</param>
		/// <param name="extensions">The file extensions to register the definition for.</param>
		/// <param name="highlighting">The highlighting definition.</param>
		public void RegisterHighlighting(string name, string[] extensions, IHighlightingDefinition highlighting)
		{
			if (highlighting == null)
				throw new ArgumentNullException("highlighting");
			
			lock (lockObj) {
				allHighlightings.Add(highlighting);
				if (name != null) {
					highlightingsByName[name] = highlighting;
				}
				if (extensions != null) {
					foreach (string ext in extensions) {
						highlightingsByExtension[ext] = highlighting;
					}
				}
			}
		}
		
		/// <summary>
		/// Registers a highlighting definition.
		/// </summary>
		/// <param name="name">The name to register the definition with.</param>
		/// <param name="extensions">The file extensions to register the definition for.</param>
		/// <param name="lazyLoadedHighlighting">A function that loads the highlighting definition.</param>
		public void RegisterHighlighting(string name, string[] extensions, Func<IHighlightingDefinition> lazyLoadedHighlighting)
		{
			if (lazyLoadedHighlighting == null)
				throw new ArgumentNullException("lazyLoadedHighlighting");
			RegisterHighlighting(name, extensions, new DelayLoadedHighlightingDefinition(name, lazyLoadedHighlighting));
		}

		/// <summary>
		/// Gets the default HighlightingManager instance.
		/// The default HighlightingManager comes with built-in highlightings.
		/// </summary>
		//todo: SPAST
		private static HighlightingManager m_Instance;
		public static HighlightingManager Instance {
			get {
				if (m_Instance == null) 
				{
					if (Directory.Exists("themes"))
						m_Instance = FileSystemBasedHighlightingManager.Instance;
					else 
						m_Instance = DefaultHighlightingManager.Instance;
				}

				return m_Instance;
			}
		}

		internal sealed class FileSystemBasedHighlightingManager : DefaultHighlightingManager 
		{
			public new static readonly FileSystemBasedHighlightingManager Instance = new FileSystemBasedHighlightingManager();

			public FileSystemBasedHighlightingManager() {
				Resources.RegisterBuiltInHighlightings(this);
			}

			internal override void RegisterHighlighting(string name, string[] extensions, string resourceName) {
				try {
					RegisterHighlighting(name, extensions, LoadHighlighting(resourceName));
				} catch (HighlightingDefinitionInvalidException ex) {
					throw new InvalidOperationException("The file highlighting '" + name + "' is invalid.", ex);
				}
			}

			Func<IHighlightingDefinition> LoadHighlighting(string fileName) {
				Func<IHighlightingDefinition> func = delegate {
					Xshd.XshdSyntaxDefinition xshd;
					var file = Path.Combine("themes", fileName);
					//todo: fallback if file does not exist?
					using (Stream s = File.Open(file, FileMode.Open)) {
						using (XmlTextReader reader = new XmlTextReader(s)) {
							// in release builds, skip validating the built-in highlightings
							xshd = Xshd.HighlightingLoader.LoadXshd(reader, true);
						}
					}
					return Xshd.HighlightingLoader.Load(xshd, this);
				};
				return func;
			}
		}

		internal class DefaultHighlightingManager : HighlightingManager
		{
			public new static readonly DefaultHighlightingManager Instance = new DefaultHighlightingManager();
			
			public DefaultHighlightingManager()
			{
				Resources.RegisterBuiltInHighlightings(this);
			}
			
			// Registering a built-in highlighting
			internal virtual void RegisterHighlighting(string name, string[] extensions, string resourceName) {
				try {
					#if DEBUG
					// don't use lazy-loading in debug builds, show errors immediately
					Xshd.XshdSyntaxDefinition xshd;
					using (Stream s = Resources.OpenStream(resourceName)) {
						using (XmlTextReader reader = new XmlTextReader(s)) {
							xshd = Xshd.HighlightingLoader.LoadXshd(reader, false);
						}
					}
					Debug.Assert(name == xshd.Name);
					if (extensions != null)
						Debug.Assert(System.Linq.Enumerable.SequenceEqual(extensions, xshd.Extensions));
					else
						Debug.Assert(xshd.Extensions.Count == 0);
					
					// round-trip xshd:
//					string resourceFileName = Path.Combine(Path.GetTempPath(), resourceName);
//					using (XmlTextWriter writer = new XmlTextWriter(resourceFileName, System.Text.Encoding.UTF8)) {
//						writer.Formatting = Formatting.Indented;
//						new Xshd.SaveXshdVisitor(writer).WriteDefinition(xshd);
//					}
//					using (FileStream fs = File.Create(resourceFileName + ".bin")) {
//						new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(fs, xshd);
//					}
//					using (FileStream fs = File.Create(resourceFileName + ".compiled")) {
//						new System.Runtime.Serialization.Formatters.Binary.BinaryFormatter().Serialize(fs, Xshd.HighlightingLoader.Load(xshd, this));
//					}
					
					RegisterHighlighting(name, extensions, Xshd.HighlightingLoader.Load(xshd, this));
					#else
					RegisterHighlighting(name, extensions, LoadHighlighting(resourceName));
					#endif
				} catch (HighlightingDefinitionInvalidException ex) {
					throw new InvalidOperationException("The built-in highlighting '" + name + "' is invalid.", ex);
				}
			}
			
			[System.Diagnostics.CodeAnalysis.SuppressMessage("Microsoft.Performance", "CA1811:AvoidUncalledPrivateCode",
			                                                 Justification = "LoadHighlighting is used only in release builds")]
			Func<IHighlightingDefinition> LoadHighlighting(string resourceName)
			{
				Func<IHighlightingDefinition> func = delegate {
					Xshd.XshdSyntaxDefinition xshd;
					//var file = Path.Combine("themes", resourceName);
					//using (Stream s = File.Open(file, FileMode.Open)) {
					using (Stream s = Resources.OpenStream(resourceName)) {
						using (XmlTextReader reader = new XmlTextReader(s)) {
							// in release builds, skip validating the built-in highlightings
							xshd = Xshd.HighlightingLoader.LoadXshd(reader, true);
						}
					}
					return Xshd.HighlightingLoader.Load(xshd, this);
				};
				return func;
			}
		}
	}
}
