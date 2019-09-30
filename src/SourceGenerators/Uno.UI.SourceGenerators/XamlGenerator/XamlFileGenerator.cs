﻿using Uno.Extensions;
using Uno.MsBuildTasks.Utils;
using Uno.MsBuildTasks.Utils.XamlPathParser;
using Uno.UI.SourceGenerators.XamlGenerator.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Uno.Roslyn;
using System.Threading;
using Uno;
using Uno.Equality;
using Uno.Logging;
using Uno.UI.SourceGenerators.XamlGenerator.XamlRedirection;

namespace Uno.UI.SourceGenerators.XamlGenerator
{
	internal partial class XamlFileGenerator
	{
		private const string ImplicitStyleMarker = "__ImplicitStyle_";
		private const string GlobalPrefix = "global::";
		private const string QualifiedNamespaceMarker = ".";

		private readonly Dictionary<string, string[]> _knownNamespaces = new Dictionary<string, string[]>
		{
			{
				XamlConstants.PresentationXamlXmlNamespace,
				XamlConstants.Namespaces.PresentationNamespaces
			},
			{
				XamlConstants.XamlXmlNamespace,
				new [] {
					"System",
				}
			},
		};

		private readonly Dictionary<string, XamlObjectDefinition> _namedResources = new Dictionary<string, XamlObjectDefinition>();
		private readonly List<string> _partials = new List<string>();
		/// <summary>
		/// Names of generated properties associated with resource definitions. These are created for top-level ResourceDictionary declarations only.
		/// </summary>
		private readonly Dictionary<(string Theme, string ResourceKey), string> _topLevelDictionaryProperties = new Dictionary<(string, string), string>();
		private readonly Stack<NameScope> _scopeStack = new Stack<NameScope>();
		private readonly XamlFileDefinition _fileDefinition;
		private readonly string _targetPath;
		private readonly string _defaultNamespace;
		private readonly RoslynMetadataHelper _metadataHelper;
		private readonly string _fileUniqueId;
		private readonly DateTime _lastReferenceUpdateTime;
		private readonly string[] _analyzerSuppressions;
		private readonly string[] _resourceKeys;
		private readonly bool _outputSourceComments;
		private int _applyIndex = 0;
		private int _collectionIndex = 0;
		private int _subclassIndex = 0;
		private int _dictionaryPropertyIndex = 0;
		private string _themeDictionaryCurrentlyBuilding;
		private readonly XamlGlobalStaticResourcesMap _globalStaticResourcesMap;
		private readonly bool _isUiAutomationMappingEnabled;
		private readonly Dictionary<string, string[]> _uiAutomationMappings;
		private readonly string _defaultLanguage;
		private readonly bool _isDebug;
		private readonly string _relativePath;

		// Determines if the source generator will skip the inclusion of UseControls in the
		// visual tree. See https://github.com/unoplatform/uno/issues/61
		private readonly bool _skipUserControlsInVisualTree;

		private readonly List<INamedTypeSymbol> _xamlAppliedTypes = new List<INamedTypeSymbol>();

		private readonly INamedTypeSymbol _elementStubSymbol;
		private readonly INamedTypeSymbol _contentPresenterSymbol;
		private readonly INamedTypeSymbol _stringSymbol;
		private readonly INamedTypeSymbol _objectSymbol;
		private readonly INamedTypeSymbol _iFrameworkElementSymbol;
		private readonly INamedTypeSymbol _dependencyObjectSymbol;
		private readonly INamedTypeSymbol _markupExtensionSymbol;

		private readonly INamedTypeSymbol _iCollectionSymbol;
		private readonly INamedTypeSymbol _iCollectionOfTSymbol;
		private readonly INamedTypeSymbol _iConvertibleSymbol;
		private readonly INamedTypeSymbol _iListSymbol;
		private readonly INamedTypeSymbol _iListOfTSymbol;
		private readonly INamedTypeSymbol _iDictionaryOfTKeySymbol;
		private readonly INamedTypeSymbol _dataBindingSymbol;
		private readonly INamedTypeSymbol _styleSymbol;

		private readonly List<INamedTypeSymbol> _markupExtensionTypes;

		private readonly bool _isWasm;

		static XamlFileGenerator()
		{
			_findContentProperty = Funcs.Create<INamedTypeSymbol, IPropertySymbol>(SourceFindContentProperty).AsLockedMemoized();
			_isAttachedProperty = Funcs.Create<INamedTypeSymbol, string, bool>(SourceIsAttachedProperty).AsLockedMemoized();
		}

		public XamlFileGenerator(
			XamlFileDefinition file,
			string targetPath,
			string defaultNamespace,
			RoslynMetadataHelper metadataHelper,
			string fileUniqueId,
			DateTime lastReferenceUpdateTime,
			string[] analyzerSuppressions,
			string[] resourceKeys,
			bool outputSourceComments,
			XamlGlobalStaticResourcesMap globalStaticResourcesMap,
			bool isUiAutomationMappingEnabled,
			Dictionary<string, string[]> uiAutomationMappings,
			string defaultLanguage,
			bool isWasm,
			bool isDebug,
			bool skipUserControlsInVisualTree
		)
		{
			_fileDefinition = file;
			_targetPath = targetPath;
			_defaultNamespace = defaultNamespace;
			_metadataHelper = metadataHelper;
			_fileUniqueId = fileUniqueId;
			_lastReferenceUpdateTime = lastReferenceUpdateTime;
			_analyzerSuppressions = analyzerSuppressions;
			_resourceKeys = resourceKeys;
			_outputSourceComments = outputSourceComments;
			_globalStaticResourcesMap = globalStaticResourcesMap;
			_isUiAutomationMappingEnabled = isUiAutomationMappingEnabled;
			_uiAutomationMappings = uiAutomationMappings;
			_defaultLanguage = defaultLanguage.HasValue() ? defaultLanguage : "en-US";
			_isDebug = isDebug;
			_skipUserControlsInVisualTree = skipUserControlsInVisualTree;

			InitCaches();

			_relativePath = PathHelper.GetRelativePath(_targetPath, _fileDefinition.FilePath);
			_stringSymbol = GetType("System.String");
			_objectSymbol = GetType("System.Object");
			_elementStubSymbol = GetType(XamlConstants.Types.ElementStub);
			_contentPresenterSymbol = GetType(XamlConstants.Types.ContentPresenter);
			_iFrameworkElementSymbol = GetType(XamlConstants.Types.IFrameworkElement);
			_dependencyObjectSymbol = GetType(XamlConstants.Types.DependencyObject);
			_markupExtensionSymbol = GetType(XamlConstants.Types.MarkupExtension);
			_iCollectionSymbol = GetType("System.Collections.ICollection");
			_iCollectionOfTSymbol = GetType("System.Collections.Generic.ICollection`1");
			_iConvertibleSymbol = GetType("System.IConvertible");
			_iListSymbol = GetType("System.Collections.IList");
			_iListOfTSymbol = GetType("System.Collections.Generic.IList`1");
			_iDictionaryOfTKeySymbol = GetType("System.Collections.Generic.IDictionary`2");
			_dataBindingSymbol = GetType("Windows.UI.Xaml.Data.Binding");
			_styleSymbol = GetType(XamlConstants.Types.Style);

			_markupExtensionTypes = _medataHelper.GetAllTypesDerivingFrom(_markupExtensionSymbol).ToList();

			_isWasm = isWasm;
		}

		/// <summary>
		/// Indicates if the code generation should write #error in the generated code (and break at compile time) or write a // Warning, which would be silent.
		/// </summary>
		/// <remarks>Initial behavior is to write // Warning, hence the default value to false, but we suggest setting this to true.</remarks>
		public static bool ShouldWriteErrorOnInvalidXaml { get; set; } = false;

		public string GenerateFile()
		{
			this.Log().Info("Processing file {0}".InvariantCultureFormat(_fileDefinition.FilePath));

			var partialFileName = Path.GetFileNameWithoutExtension(_fileDefinition.FilePath);

			// Check for the Roslyn generator's output location
			var outputFile = Path.Combine(
				_targetPath,
				$@"g\{typeof(XamlCodeGenerator).Name}\{_fileDefinition.UniqueID}.g.cs"
				);

			if (File.Exists(outputFile))
			{
				var outputInfo = new FileInfo(outputFile);
				var inputInfo = new FileInfo(_fileDefinition.FilePath);

				if (
					outputInfo.LastWriteTime >= inputInfo.LastWriteTime

					// Check for the references update time. If the file has been generated before the last write time
					// on the references, regenerate the output.
					&& outputInfo.LastWriteTime > _lastReferenceUpdateTime

					// Empty files should be regenerated
					&& outputInfo.Length != 0
				)
				{
					this.Log().Info("Skipping unmodified file {0}".InvariantCultureFormat(_fileDefinition.FilePath));
					return File.ReadAllText(outputFile);
				}
			}

			try
			{
				return InnerGenerateFile();
			}
			catch (Exception e)
			{
				throw new Exception("Processing failed for file {0}".InvariantCultureFormat(_fileDefinition.FilePath), e);
			}
		}

		private string InnerGenerateFile()
		{
			var writer = new IndentedStringBuilder();

			writer.AppendLineInvariant("// <autogenerated />");
			writer.AppendLineInvariant("// Xaml Source Generation is using the {0} Xaml Parser", XamlRedirection.XamlConfig.IsUnoXaml ? "Uno.UI" : "System");
			writer.AppendLineInvariant("#pragma warning disable 618 // Ignore obsolete members warnings");
			writer.AppendLineInvariant("#pragma warning disable 105 // Ignore duplicate namespaces");
			writer.AppendLineInvariant("using System;");
			writer.AppendLineInvariant("using System.Collections.Generic;");
			writer.AppendLineInvariant("using System.Diagnostics;");
			writer.AppendLineInvariant("using System.Linq;");
			writer.AppendLineInvariant("using Uno.UI;");

			//TODO Determine the list of namespaces to use
			foreach (var ns in XamlConstants.Namespaces.All)
			{
				writer.AppendLineInvariant($"using {ns};");
			}

			writer.AppendLineInvariant("using Uno.Extensions;");
			writer.AppendLineInvariant("using Uno;");

			writer.AppendLineInvariant("using {0};", _defaultNamespace);

			// For Subclass build functionality
			writer.AppendLineInvariant("");
			writer.AppendLineInvariant("#if __ANDROID__");
			writer.AppendLineInvariant("using _View = Android.Views.View;");
			writer.AppendLineInvariant("#elif __IOS__");
			writer.AppendLineInvariant("using _View = UIKit.UIView;");
			writer.AppendLineInvariant("#elif __MACOS__");
			writer.AppendLineInvariant("using _View = AppKit.NSView;");
			writer.AppendLineInvariant("#elif __WASM__");
			writer.AppendLineInvariant("using _View = Windows.UI.Xaml.UIElement;");
			writer.AppendLineInvariant("#elif NET461");
			writer.AppendLineInvariant("using _View = Windows.UI.Xaml.UIElement;");
			writer.AppendLineInvariant("#endif");

			writer.AppendLineInvariant("");

			var topLevelControl = _fileDefinition.Objects.First();

			if (topLevelControl.Type.Name == "ResourceDictionary")
			{
				BuildEmptyBackingClass(writer, topLevelControl);

				BuildTopLevelResourceDictionary(writer, topLevelControl);
			}
			else
			{
				_className = GetClassName(topLevelControl);

				using (writer.BlockInvariant("namespace {0}", _className.ns))
				{
					AnalyzerSuppressionsGenerator.Generate(writer, _analyzerSuppressions);

					var controlBaseType = GetType(topLevelControl.Type);

					using (writer.BlockInvariant("public sealed partial class {0} : {1}", _className.className, controlBaseType.ToDisplayString()))
					{
						var isDirectUserControlChild = IsUserControl(topLevelControl.Type, checkInheritance: false);

						using (Scope("{0}{1}".InvariantCultureFormat(_className.ns.Replace(".", ""), _className.className)))
						{
							using (writer.BlockInvariant(BuildControlInitializerDeclaration(_className.className, topLevelControl)))
							{
								if (IsApplication(topLevelControl.Type))
								{
									BuildApplicationInitializerBody(writer, topLevelControl);
								}
								else
								{
									BuildGenericControlInitializerBody(writer, topLevelControl, isDirectUserControlChild);
									BuildNamedResources(writer, _namedResources);
								}

								if (isDirectUserControlChild)
								{
									writer.AppendLineInvariant("return content;");
								}
							}

							if (isDirectUserControlChild)
							{
								using (writer.BlockInvariant("public {0}(bool skipsInitializeComponents)", _className.className))
								{
								}

								using (writer.BlockInvariant("private void InitializeComponent()"))
								{
									writer.AppendLineInvariant("Content = (_View)GetContent();");
								}
							}

							BuildPartials(writer, isStatic: false);

							BuildBackingFields(writer);

							BuildChildSubclasses(writer);

						}

						BuildInitializeXamlOwner(writer);
					}
				}
			}

			BuildXamlApplyBlocks(writer);

			// var formattedCode = ReformatCode(writer.ToString());
			return writer.ToString();
		}

		private void BuildInitializeXamlOwner(IndentedStringBuilder writer)
		{
			using (writer.BlockInvariant("private void InitializeXamlOwner()"))
			{
				if (_hasLiteralEventsRegistration)
				{
					// We only propagate the xaml owner in the tree if there are event registrations in the file
					// to avoid the performance impact of having a unused dependency property.
					writer.AppendLineInvariant("global::Uno.UI.Xaml.XamlInfo.SetXamlInfo(this, new global::Uno.UI.Xaml.XamlInfo(this));");
				}
			}
		}

		private void BuildXamlApplyBlocks(IndentedStringBuilder writer)
		{
			using (writer.BlockInvariant("namespace {0}", _defaultNamespace))
			{
				using (writer.BlockInvariant("static class {0}XamlApplyExtensions", _fileUniqueId))
				{
					foreach (var typeInfo in _xamlAppliedTypes.Select((type, i) => new { type, Index = i }))
					{
						writer.AppendLineInvariant($"public delegate void XamlApplyHandler{typeInfo.Index}({GetGlobalizedTypeName(typeInfo.type.ToString())} instance);");

						using (writer.BlockInvariant(
							$"public static {GetGlobalizedTypeName(typeInfo.type.ToString())} {_fileUniqueId}_XamlApply(this {GetGlobalizedTypeName(typeInfo.type.ToString())} instance, XamlApplyHandler{typeInfo.Index} handler)"
						))
						{
							writer.AppendLineInvariant($"handler(instance);");
							writer.AppendLineInvariant($"return instance;");
						}
					}
				}
			}
		}

		private void BuildApplicationInitializerBody(IndentedStringBuilder writer, XamlObjectDefinition topLevelControl)
		{
			writer.AppendLineInvariant($"global::Windows.UI.Xaml.GenericStyles.Initialize();");
			GenerateResourceLoader(writer);
			writer.AppendLineInvariant($"global::{_defaultNamespace}.GlobalStaticResources.Initialize();");
			writer.AppendLineInvariant($"global::Uno.UI.DataBinding.BindableMetadata.Provider = new global::{_defaultNamespace}.BindableMetadataProvider();");


			writer.AppendLineInvariant($"#if __ANDROID__");
			writer.AppendLineInvariant($"global::Uno.Helpers.DrawableHelper.Drawables = typeof(global::{_defaultNamespace}.Resource.Drawable);");
			writer.AppendLineInvariant($"#endif");

			RegisterAndBuildResources(writer, topLevelControl, isInInitializer: false);
			BuildProperties(writer, topLevelControl, isInline: false, returnsContent: false);

			writer.AppendLineInvariant("");
			writer.AppendLineInvariant("this");

			using (var blockWriter = CreateApplyBlock(writer, null, out var closure))
			{
				blockWriter.AppendLineInvariant(
					"// Source {0} (Line {1}:{2})",
					_fileDefinition.FilePath,
					topLevelControl.LineNumber,
					topLevelControl.LinePosition
				);
				BuildLiteralProperties(blockWriter, topLevelControl, closure);
			}

			writer.AppendLineInvariant(";");
			if (_isUiAutomationMappingEnabled)
			{
				writer.AppendLineInvariant("global::Uno.UI.FrameworkElementHelper.IsUiAutomationMappingEnabled = true;");
			}
		}

		private void GenerateResourceLoader(IndentedStringBuilder writer)
		{
			writer.AppendLineInvariant($"global::Windows.ApplicationModel.Resources.ResourceLoader.DefaultLanguage = \"{_defaultLanguage}\";");

			writer.AppendLineInvariant($"global::Windows.ApplicationModel.Resources.ResourceLoader.AddLookupAssembly(GetType().Assembly);");

			foreach (var reference in _metadataHelper.Compilation.ExternalReferences)
			{
				if (!File.Exists(reference.Display))
				{
					throw new InvalidOperationException($"The reference {reference.Display} could not be found in {reference.Display}");
				}

				var asm = Mono.Cecil.AssemblyDefinition.ReadAssembly(reference.Display);

				if (asm.MainModule.HasResources && asm.MainModule.Resources.Any(r => r.Name.EndsWith("upri")))
				{
					writer.AppendLineInvariant($"global::Windows.ApplicationModel.Resources.ResourceLoader.AddLookupAssembly(global::System.Reflection.Assembly.Load(\"{asm.FullName}\"));");
				}
			}
		}

		private void BuildGenericControlInitializerBody(IndentedStringBuilder writer, XamlObjectDefinition topLevelControl, bool isDirectUserControlChild)
		{
			// OnInitializeCompleted() seems to be used by some older code as a substitute for the constructor for UserControls, which are optimized out of the visual tree.
			RegisterPartial("void OnInitializeCompleted()");

			writer.AppendLineInvariant("var nameScope = new global::Windows.UI.Xaml.NameScope();");
			writer.AppendLineInvariant("NameScope.SetNameScope(this, nameScope);");

			RegisterAndBuildResources(writer, topLevelControl, isInInitializer: false);
			BuildProperties(writer, topLevelControl, isInline: false, returnsContent: isDirectUserControlChild);

			writer.AppendLineInvariant(";");

			writer.AppendLineInvariant("");
			writer.AppendLineInvariant(isDirectUserControlChild ? "content" : "this");

			using (var blockWriter = CreateApplyBlock(writer, null, out var closureName))
			{
				blockWriter.AppendLineInvariant(
					"// Source {0} (Line {1}:{2})",
					_fileDefinition.FilePath,
					topLevelControl.LineNumber,
					topLevelControl.LinePosition
				);

				BuildLiteralProperties(blockWriter, topLevelControl, closureName);
			}

			if (IsFrameworkElement(topLevelControl.Type))
			{
				BuildExtendedProperties(writer, topLevelControl, isDirectUserControlChild, useGenericApply: true);
			}

			writer.AppendLineInvariant(";");

			writer.AppendLineInvariant("OnInitializeCompleted();");
			writer.AppendLineInvariant("InitializeXamlOwner();");
		}

		private void BuildPartials(IIndentedStringBuilder writer, bool isStatic)
		{
			foreach (var partialDefinition in _partials)
			{
				writer.AppendLineInvariant("{0}partial " + partialDefinition + ";", isStatic ? "static " : "");
			}
		}

		private void BuildBackingFields(IIndentedStringBuilder writer)
		{
			foreach (var backingFieldDefinition in CurrentScope.BackingFields)
			{
				var sanitizedFieldName = SanitizeResourceName(backingFieldDefinition.Name);

				CurrentScope.ReferencedElementNames.Remove(sanitizedFieldName);

				writer.AppendLineInvariant($"private global::Windows.UI.Xaml.Data.ElementNameSubject _{sanitizedFieldName}Subject = new global::Windows.UI.Xaml.Data.ElementNameSubject();");


				using (writer.BlockInvariant($"{FormatAccessibility(backingFieldDefinition.Accessibility)} {GetGlobalizedTypeName(backingFieldDefinition.Type.ToString())} {sanitizedFieldName}"))
				{
					using (writer.BlockInvariant("get"))
					{
						writer.AppendLineInvariant($"return ({GetGlobalizedTypeName(backingFieldDefinition.Type.ToString())})_{sanitizedFieldName}Subject.ElementInstance;");
					}

					using (writer.BlockInvariant("set"))
					{
						writer.AppendLineInvariant($"_{sanitizedFieldName}Subject.ElementInstance = value;");
					}
				}
			}

			foreach (var remainingReference in CurrentScope.ReferencedElementNames)
			{
				// Create load-time subjects for ElementName references not in local scope
				writer.AppendLineInvariant($"private global::Windows.UI.Xaml.Data.ElementNameSubject _{remainingReference}Subject = new global::Windows.UI.Xaml.Data.ElementNameSubject(isRuntimeBound: true, name: \"{remainingReference}\");");
			}
		}

		private static readonly char[] ResourceInvalidCharacters = new[] { '.', '-' };

		private static string SanitizeResourceName(string name)
		{
			foreach (var c in ResourceInvalidCharacters)
			{
				name = name.Replace(c, '_');
			}

			return name;
		}

		private void BuildChildSubclasses(IIndentedStringBuilder writer, bool isTopLevel = false)
		{
			var disposable = isTopLevel ? writer.BlockInvariant("namespace {0}.__Resources", _defaultNamespace) : null;

			using (disposable)
			{
				foreach (var kvp in CurrentScope.Subclasses)
				{
					var className = kvp.Key;
					var contentOwner = kvp.Value.ContentOwner;

					using (Scope(className))
					{
						var classAccessibility = isTopLevel ? "" : "private";

						using (writer.BlockInvariant($"{classAccessibility} class {className}"))
						{
							using (writer.BlockInvariant("public {0} Build()", kvp.Value.ReturnType))
							{
								writer.AppendLineInvariant("var nameScope = new global::Windows.UI.Xaml.NameScope();");
								writer.AppendLineInvariant("var child = ");

								// Is never considered in Global Resources because class encapsulation
								BuildChild(writer, contentOwner, contentOwner.Objects.First());

								writer.AppendLineInvariant(";");
								writer.AppendLineInvariant("if (child is DependencyObject d) Windows.UI.Xaml.NameScope.SetNameScope(d, nameScope);");

								writer.AppendLineInvariant("return child;");
							}

							BuildBackingFields(writer);

							BuildChildSubclasses(writer);
						}
					}
				}
			}
		}

		private string BuildControlInitializerDeclaration(string className, XamlObjectDefinition topLevelControl)
		{
			if (IsPage(topLevelControl.Type))
			{
				return "protected override void InitializeComponent()";
			}
			else if (IsUserControl(topLevelControl.Type, checkInheritance: false))
			{
				string contentTypeDisplayString = GetImplicitChildTypeDisplayString(topLevelControl);

				return "public {0} GetContent()".InvariantCultureFormat(contentTypeDisplayString);
			}
			else
			{
				return "private void InitializeComponent()";
			}
		}

		private string GetImplicitChildTypeDisplayString(XamlObjectDefinition topLevelControl)
		{
			var contentType = FindImplicitContentMember(topLevelControl)?.Objects.FirstOrDefault()?.Type;

			return contentType == null ? XamlConstants.Types.IFrameworkElement : FindType(contentType)?.ToDisplayString() ?? XamlConstants.Types.IFrameworkElement;
		}

		/// <summary>
		/// Processes a top-level ResourceDictionary declaration.
		/// </summary>
		private void BuildTopLevelResourceDictionary(IIndentedStringBuilder writer, XamlObjectDefinition topLevelControl)
		{
			var globalResources = new Dictionary<string, XamlObjectDefinition>();

			using (Scope(Path.GetFileNameWithoutExtension(_fileDefinition.FilePath).Replace(".", "_") + "RD"))
			{
				using (writer.BlockInvariant("namespace {0}", _defaultNamespace))
				{
					AnalyzerSuppressionsGenerator.Generate(writer, _analyzerSuppressions);
					using (writer.BlockInvariant("public sealed partial class GlobalStaticResources"))
					{
						globalResources.Merge(ImportResourceDictionary(writer, topLevelControl));

						BuildPartials(writer, isStatic: true);
						BuildResourceDictionaryGlobalProperties(writer, topLevelControl);

						var themeDictionaryMember = topLevelControl.Members.FirstOrDefault(m => m.Member.Name == "ThemeDictionaries");

						foreach (var dict in (themeDictionaryMember?.Objects).Safe())
						{
							BuildResourceDictionaryGlobalProperties(writer, dict);
						}

						writer.AppendLineInvariant("private static global::Windows.UI.Xaml.ResourceDictionary _{0}_ResourceDictionary;", _fileUniqueId);
						writer.AppendLine();
						using (writer.BlockInvariant("internal static global::Windows.UI.Xaml.ResourceDictionary {0}_ResourceDictionary", _fileUniqueId))
						{
							using (writer.BlockInvariant("get"))
							{
								using (writer.BlockInvariant("if (_{0}_ResourceDictionary == null)", _fileUniqueId))
								{
									writer.AppendLineInvariant("_{0}_ResourceDictionary = ", _fileUniqueId);
									InitializeAndBuildResourceDictionary(writer, topLevelControl);
									writer.AppendLineInvariant(";");
									var url = _globalStaticResourcesMap.GetSourceLink(_fileDefinition);
									writer.AppendLineInvariant("_{0}_ResourceDictionary.Source = new Uri(\"ms-resource:///Files/{1}\");", _fileUniqueId, url);

								}

								writer.AppendLineInvariant("return _{0}_ResourceDictionary;", _fileUniqueId);
							}
						}

						BuildDefaultStylesRegistration(writer, FindImplicitContentMember(topLevelControl));
					}
				}

				BuildChildSubclasses(writer, isTopLevel: true);
			}
		}

		/// <summary>
		///Builds global static properties for each resource in a ResourceDictionary, for intra-dictionary static lookup.
		/// </summary>
		/// <param name="writer">The writer to use</param>
		/// <param name="dictionaryObject">The <see cref="XamlObjectDefinition"/> associated with the dictionary.</param>
		private void BuildResourceDictionaryGlobalProperties(IIndentedStringBuilder writer, XamlObjectDefinition dictionaryObject)
		{
			var resourcesRoot = FindImplicitContentMember(dictionaryObject);
			var theme = GetDictionaryResourceKey(dictionaryObject);
			var resources = (resourcesRoot?.Objects).Safe();

			// Pre-populate property names (in case of forward lexical references)
			var index = _dictionaryPropertyIndex;
			foreach (var resource in resources)
			{
				var key = GetDictionaryResourceKey(resource);

				if (key == null)
				{
					continue;
				}

				if (resource.Type.Name == "StaticResource")
				{
					continue; //TODO: try to handle this
				}

				index++;
				var propertyName = GetPropertyNameForResourceKey(index);
				_topLevelDictionaryProperties[(theme, key)] = propertyName;
			}

			//Create static properties
			var former = _themeDictionaryCurrentlyBuilding; //Will 99% of the time be null. (Mainly this is half-heartedly trying to support funky usage of recursive merged dictionaries.)
			_themeDictionaryCurrentlyBuilding = theme;
			foreach (var resource in resources)
			{
				var key = GetDictionaryResourceKey(resource);

				if (key == null)
				{
					continue;
				}

				if (resource.Type.Name == "StaticResource")
				{
					continue; //TODO: try to handle this
				}

				_dictionaryPropertyIndex++;
				var propertyName = GetPropertyNameForResourceKey(_dictionaryPropertyIndex);
				if (_topLevelDictionaryProperties[(theme, key)] != propertyName)
				{
					throw new InvalidOperationException("Property was not created correctly.");
				}
				writer.AppendLineInvariant("// Property for resource {0} {1}", key, theme != null ? "in theme {0}".InvariantCultureFormat(theme) : "");
				BuildSingleTimeInitializer(writer, resource.Type.Name, propertyName, () => BuildChild(writer, resourcesRoot, resource));
			}
			_themeDictionaryCurrentlyBuilding = former;
		}

		/// <summary>
		/// Get name to use for global static property associated with a resource.
		/// </summary>
		/// <param name="index">An index associated with the property.</param>
		private string GetPropertyNameForResourceKey(int index)
		{
			return "Entry{0}_{1}".InvariantCultureFormat(_fileDefinition.ShortId, index);
		}

		/// <summary>
		/// Get the dictionary key set on a Xaml object, if any. This may be defined by x:Key or alternately x:Name.
		/// </summary>
		private string GetDictionaryResourceKey(XamlObjectDefinition resource) => GetDictionaryResourceKey(resource, out var _);

		private string GetDictionaryResourceKey(XamlObjectDefinition resource, out string name)
			=> GetExplicitDictionaryResourceKey(resource, out name)
			?? GetImplicitDictionaryResourceKey(resource);


		/// <summary>
		/// Get the dictionary key set on a Xaml object, if any. This may be defined by x:Key or alternately x:Name.
		/// </summary>
		/// <param name="resource">The Xaml object.</param>
		/// <param name="name">The x:Name defined on the object, if any, returned as an out parameter.</param>
		/// <returns>The key to use if one is defined, otherwise null.</returns>
		private static string GetExplicitDictionaryResourceKey(XamlObjectDefinition resource, out string name)
		{
			var keyDef = resource.Members.FirstOrDefault(m => m.Member.Name == "Key");
			var nameDef = resource.Members.FirstOrDefault(m => m.Member.Name == "Name");
			name = nameDef?.Value?.ToString();
			var key = keyDef?.Value?.ToString() ?? name;
			return key;
		}

		/// <summary>
		/// Get the implicit key for a dictionary resource, if any.
		///
		/// An implicit key is the TargetType of a Style resource.
		/// </summary>
		private string GetImplicitDictionaryResourceKey(XamlObjectDefinition resource)
		{
			if (resource.Type.Name == "Style"
				&& resource.Members.FirstOrDefault(m => m.Member.Name == "TargetType")?.Value is string targetTypeName)
			{
				var targetType = GetType(targetTypeName);
				return "typeof({0})".InvariantCultureFormat(GetGlobalizedTypeName(targetType.ToDisplayString()));
			}

			return null;
		}

		/// <summary>
		/// Builds registrations for the default styles defined in this ResourceDictionary.
		///
		/// Note: if we're currently building an app, these registrations will never actually be called (the styles will be treated as implicit styles
		/// instead).
		/// </summary>
		private void BuildDefaultStylesRegistration(IIndentedStringBuilder writer, XamlMemberDefinition resourcesRoot)
		{
			var stylesCandidates = resourcesRoot?.Objects.Where(o => o.Type.Name == "Style" && GetExplicitDictionaryResourceKey(o, out var _) == null);
			if (stylesCandidates?.None() ?? true)
			{
				return;
			}

			writer.AppendLine();

			using (writer.BlockInvariant("static partial void RegisterDefaultStyles_{0}()", _fileUniqueId))
			{
				foreach (var style in stylesCandidates)
				{
					var targetTypeMember = style.Members.FirstOrDefault(m => m.Member.Name == "TargetType");
					if (!(targetTypeMember?.Value is string targetTypeName))
					{
						continue;
					}

					var targetType = GetType(targetTypeName);
					var implicitKey = GetImplicitDictionaryResourceKey(style);
					if (targetType.ContainingAssembly == _metadataHelper.Compilation.Assembly)
					{
						writer.AppendLineInvariant("global::Windows.UI.Xaml.Style.RegisterDefaultStyleForType({0}, () => {1});",
										implicitKey,
										GetResourceDictionaryPropertyName(implicitKey)
									);
					}
					else
					{
						writer.AppendLineInvariant("// Skipping style registration for {0} because the type is defined in assembly {1}", implicitKey, targetType.ContainingAssembly.Name);
					}
				}
			}
		}

		/// <summary>
		/// Initialize a new ResourceDictionary instance and populate its items and properties.
		/// </summary>
		private void InitializeAndBuildResourceDictionary(IIndentedStringBuilder writer, XamlObjectDefinition topLevelControl)
		{
			writer.AppendLineInvariant("new ResourceDictionary {{");
			BuildResourceDictionary(writer, FindImplicitContentMember(topLevelControl), isInInitializer: true);
			BuildMergedDictionaries(writer, topLevelControl.Members.FirstOrDefault(m => m.Member.Name == "MergedDictionaries"), isInInitializer: true);
			BuildThemeDictionaries(writer, topLevelControl.Members.FirstOrDefault(m => m.Member.Name == "ThemeDictionaries"), isInInitializer: true);
			writer.AppendLineInvariant("}}");
		}

		private void BuildEmptyBackingClass(IIndentedStringBuilder writer, XamlObjectDefinition topLevelControl)
		{
			var className = FindClassName(topLevelControl);

			if (className.ns != null)
			{
				var controlBaseType = GetType(topLevelControl.Type);

				using (writer.BlockInvariant("namespace {0}", className.ns))
				{
					using (writer.BlockInvariant("public sealed partial class {0} : {1}", className.className, GetGlobalizedTypeName(controlBaseType.ToDisplayString())))
					{
						using (writer.BlockInvariant("public void InitializeComponent()"))
						{
						}
					}
				}
			}
		}

		private Dictionary<string, XamlObjectDefinition> ImportResourceDictionary(
			IIndentedStringBuilder writer,
			XamlObjectDefinition topLevelControl
		)
		{
			var resources = new Dictionary<string, XamlObjectDefinition>();

			XamlMemberDefinition contentNode;

			if (IsApplication(topLevelControl.Type))
			{
				contentNode = topLevelControl.Members.FirstOrDefault(m => m.Member.Name == "Resources");
			}
			else
			{
				contentNode = FindMember(topLevelControl, "_UnknownContent");
			}

			if (contentNode != null)
			{
				foreach (var resource in contentNode.Objects)
				{
					var key = resource.Members.FirstOrDefault(m => m.Member.Name == "Key" || m.Member.Name == "Name");

					if (key != null)
					{
						if (!resources.ContainsKey(key.Value.ToString()))
						{
							resources.Add(key.Value.ToString(), resource);
						}
						else
						{
							throw new InvalidOperationException($"Duplicate resource {key.Value.ToString()}");
						}
					}
					else
					{
						if (resource.Type.Name == "Style")
						{
							var targetType = FindMember(resource, "TargetType")?.Value.ToString();

							if (targetType != null)
							{
								var fullTargetType = FindType(targetType).SelectOrDefault(t => t.ToDisplayString(), targetType);

								var keyName = (ImplicitStyleMarker + fullTargetType).Replace(".", "_");

								if (resources.ContainsKey(keyName))
								{
									throw new InvalidOperationException($"Implicit resource for {keyName} already exists");
								}
								else
								{
									resources.Add(keyName, resource);
								}
							}
							else
							{
								throw new InvalidOperationException("The implicit resource must have a TargetType attribute");
							}
						}
						else if (resource.Type.Name == "ResourceDictionary")
						{
							// ResourceDictionaries and MergedDictionaries are handled elsewhere
						}
						else
						{
							GenerateError(
								writer,
								"Implicit resource other than Style in inline resources are not supported ({0}, Line {1}:{2})",
								contentNode.Member.Type,
								contentNode.LineNumber,
								contentNode.LinePosition
							);
						}
					}
				}
			}

			return resources;
		}

		/// <summary>
		/// Helper for building a lazily-initialized static property.
		/// </summary>
		/// <param name="writer">The writer to use</param>
		/// <param name="propertyType">The type of the property.</param>
		/// <param name="propertyName">The name of the property.</param>
		/// <param name="propertyBodyBuilder">Function that builds the initialization logic for the property.</param>
		/// <param name="isStatic"></param>
		private void BuildSingleTimeInitializer(IIndentedStringBuilder writer, string propertyType, string propertyName, Action propertyBodyBuilder, bool isStatic = true)
		{
			// The property type may be partially qualified, try resolving it through FindType
			var propertySymbol = FindType(propertyType);
			propertyType = propertySymbol?.GetFullName() ?? propertyType;

			var sanitizedPropertyName = SanitizeResourceName(propertyName);

			var propertyInitializedVariable = "_{0}Initialized".InvariantCultureFormat(sanitizedPropertyName);
			var backingFieldVariable = "__{0}BackingField".InvariantCultureFormat(sanitizedPropertyName);
			var staticModifier = isStatic ? "static" : "";
			var publicPropertyType = FindType(propertyType)?.DeclaredAccessibility == Accessibility.Public
				? propertyType
				: "System.Object";

			writer.AppendLineInvariant($"private {staticModifier} bool {propertyInitializedVariable} = false;");
			writer.AppendLineInvariant($"private {staticModifier} {GetGlobalizedTypeName(propertyType)} {backingFieldVariable};");

			writer.AppendLine();

			using (writer.BlockInvariant($"internal {staticModifier} {GetGlobalizedTypeName(publicPropertyType)} {sanitizedPropertyName}"))
			{
				using (writer.BlockInvariant("get"))
				{
					using (writer.BlockInvariant($"if(!{propertyInitializedVariable})"))
					{
						writer.AppendLineInvariant($"{backingFieldVariable} = ");
						using (writer.Indent())
						{
							propertyBodyBuilder();
						}
						writer.AppendLineInvariant(";");
						writer.AppendLineInvariant($"{propertyInitializedVariable} = true;");
					}

					writer.AppendLineInvariant($"return {backingFieldVariable};");
				}
			}

			writer.AppendLine();
		}

		private void BuildSourceLineInfo(IIndentedStringBuilder writer, XamlObjectDefinition definition)
		{
			if (_outputSourceComments)
			{
				writer.AppendLineInvariant(
					"// Source {0} (Line {1}:{2})",
						_relativePath,
					definition.LineNumber,
					definition.LinePosition
				);
			}
		}

		private void BuildNamedResources(
			IIndentedStringBuilder writer,
			IEnumerable<KeyValuePair<string, XamlObjectDefinition>> namedResources
		)
		{
			foreach (var namedResource in namedResources)
			{
				BuildSourceLineInfo(writer, namedResource.Value);

				BuildChild(writer, null, namedResource.Value);
				writer.AppendLineInvariant(0, ";", namedResource.Value.Type);
			}

			bool IsGenerateCompiledBindings(KeyValuePair<string, XamlObjectDefinition> nr)
			{
				var type = GetType(nr.Value.Type);

				// Styles are handled differently for now, and there's no variable generated
				// for those entries. Skip the ApplyCompiledBindings for those. See
				// ImportResourceDictionary handling of x:Name for more details.
				if (type.Equals(_styleSymbol))
				{
					return false;
				}

				if (type.AllInterfaces.Any(i => i.Equals(_dependencyObjectSymbol)))
				{
					return true;
				}

				return false;
			}

			var resourcesTogenerateApplyCompiledBindings = namedResources
				.Where(IsGenerateCompiledBindings)
				.ToArray();

			if (resourcesTogenerateApplyCompiledBindings.Any())
			{
				using (writer.BlockInvariant("Loading += (s, e) =>"))
				{
					foreach (var namedResource in resourcesTogenerateApplyCompiledBindings)
					{
						var type = GetType(namedResource.Value.Type);

						writer.AppendLineInvariant($"{namedResource.Key}.ApplyCompiledBindings();");
					}
				}
				writer.AppendLineInvariant(0, ";");
			}
		}

		private bool IsSingleTimeInitializable(XamlType type)
		{
			var resourceType = GetType(type);

			return resourceType.IsValueType
				|| resourceType.SpecialType != SpecialType.None
				|| type.Name == "Double"
				|| type.Name == "Single"
				|| type.Name == "Integer"
				|| type.Name == "String"
				|| type.Name == "Boolean"
			;
		}

		private string GenerateTypeName(XamlObjectDefinition definition)
		{
			var typeName = definition.Type.Name;

			if (definition.Type.PreferredXamlNamespace.StartsWith("using:"))
			{
				typeName = definition.Type.PreferredXamlNamespace.TrimStart("using:") + "." + typeName;
			}

			// Color is an alias the base color type for the target platform.
			if (typeName == "Color" || typeName == "ListViewBaseLayoutTemplate")
			{

				return GetType(typeName).ToDisplayString();
			}

			return typeName;
		}

		private void BuildInlineStyle(IIndentedStringBuilder writer, XamlObjectDefinition style)
		{
			var targetTypeNode = GetMember(style, "TargetType");
			var targetType = targetTypeNode.Value.ToString();
			var fullTargetType = FindType(targetType).SelectOrDefault(t => t.ToDisplayString(), targetType);

			using (writer.BlockInvariant("new global::Windows.UI.Xaml.Style(typeof({0}))", GetGlobalizedTypeName(fullTargetType)))
			{
				var basedOnNode = style.Members.FirstOrDefault(o => o.Member.Name == "BasedOn");

				if (basedOnNode != null)
				{
					writer.AppendLineInvariant("BasedOn = (global::Windows.UI.Xaml.Style){0},", BuildBindingOption(basedOnNode, _styleSymbol, prependCastToType: false));
				}

				using (writer.BlockInvariant("Setters = ", fullTargetType))
				{
					var contentNode = FindMember(style, "_UnknownContent");

					var settersNode = FindMember(style, "Setters");

					if (contentNode != null && settersNode != null)
					{
						throw new InvalidOperationException($"The property 'Setters' is set more than once at line {style.LineNumber}:{style.LinePosition} ({_fileDefinition.FilePath}).");
					}

					contentNode = contentNode ?? settersNode;

					if (contentNode != null)
					{
						foreach (var child in contentNode.Objects)
						{
							if (child.Type.Name == "Setter")
							{
								var propertyNode = FindMember(child, "Property");
								var valueNode = FindMember(child, "Value");
								var property = propertyNode?.Value.SelectOrDefault(p => p.ToString());

								if (valueNode != null)
								{
									BuildPropertySetter(writer, fullTargetType, ",", property, valueNode);
								}
								else
								{
									GenerateError(writer, $"No value was specified for {property} on {child.Type.Name}");
								}
							}
							else
							{
								GenerateError(writer, "Support for {0} is not implemented", child.Type.Name);
							}
						}
					}
				}
			}
		}

		private void BuildPropertySetter(IIndentedStringBuilder writer, string fullTargetType, string lineEnding, string property, XamlMemberDefinition valueNode, string targetInstance = null)
		{
			targetInstance = targetInstance ?? "o";

			property = property?.Trim('(', ')');

			// Handle attached properties
			var isAttachedProperty = property?.Contains(".") ?? false;
			if (isAttachedProperty)
			{
				var separatorIndex = property.IndexOf(".");

				var target = property.Remove(separatorIndex);
				property = property.Substring(separatorIndex + 1);
				fullTargetType = FindType(target)?.ToDisplayString();

				if (fullTargetType == null || property == null)
				{
					GenerateError(writer, $"Property {property} on {target} could not be found.");
					return;
				}
			}

			var propertyType = FindPropertyType(fullTargetType, property);

			if (propertyType != null)
			{
				bool isDependencyProperty = IsDependencyProperty(FindType(fullTargetType), property);

				if (valueNode.Objects.None())
				{
					if (isDependencyProperty)
					{
						writer.AppendLineInvariant(
							"new global::Windows.UI.Xaml.Setter({0}.{1}Property, ({2}){3})" + lineEnding,
							GetGlobalizedTypeName(fullTargetType),
							property,
							propertyType,
							BuildLiteralValue(valueNode, propertyType)
						);
					}
					else
					{
						writer.AppendLineInvariant(
							"new global::Windows.UI.Xaml.Setter<{0}>(\"{1}\", o => {3}.{1} = {2})" + lineEnding,
							GetGlobalizedTypeName(fullTargetType),
							property,
							BuildLiteralValue(valueNode, propertyType),
							targetInstance
						);
					}
				}
				else
				{
					if (isDependencyProperty)
					{
						writer.AppendLineInvariant(
							"new global::Windows.UI.Xaml.Setter({0}.{1}Property, () => ({2})",
							GetGlobalizedTypeName(fullTargetType),
							property,
							propertyType
						);
					}
					else
					{
						writer.AppendLineInvariant(
							"new global::Windows.UI.Xaml.Setter<{0}>(\"{1}\", o => {2}.{1} = ",
							GetGlobalizedTypeName(fullTargetType),
							property,
								targetInstance
						);
					}

					if (HasMarkupExtension(valueNode))
					{
						writer.AppendLineInvariant(BuildBindingOption(valueNode, propertyType, prependCastToType: true));
					}
					else
					{
						BuildChild(writer, valueNode, valueNode.Objects.First());
					}

					writer.AppendLineInvariant(")" + lineEnding);
				}
			}
			else
			{
				GenerateError(writer, "Property for {0} cannot be found on {1}", property, fullTargetType);
			}
		}

		private void GenerateError(IIndentedStringBuilder writer, string message)
		{
			GenerateError(writer, message.Replace("{", "{{").Replace("}", "}}"), new object[0]);
		}

		private void GenerateError(IIndentedStringBuilder writer, string message, params object[] options)
		{
			if (ShouldWriteErrorOnInvalidXaml)
			{
				// it's important to add a new line to make sure #error is on its own line.
				writer.AppendLineInvariant(string.Empty);
				writer.AppendLineInvariant("#error " + message, options);
			}
			else
			{
				GenerateSilentWarning(writer, message, options);
			}

		}

		private void GenerateSilentWarning(IIndentedStringBuilder writer, string message, params object[] options)
		{
			// it's important to add a new line to make sure #error is on its own line.
			writer.AppendLineInvariant(string.Empty);
			writer.AppendLineInvariant("// WARNING " + message, options);
		}

		private bool HasMarkupExtension(XamlMemberDefinition valueNode)
		{
			// Return false if the Owner is a custom markup extension
			if (IsCustomMarkupExtensionType(valueNode.Owner?.Type))
			{
				return false;
			}

			return valueNode
				.Objects
				.Any(o =>
					o.Type.Name == "StaticResource"
					|| o.Type.Name == "ThemeResource"
					|| o.Type.Name == "Binding"
					|| o.Type.Name == "Bind"
					|| o.Type.Name == "TemplateBinding"
				);
		}

		private bool HasCustomMarkupExtension(XamlMemberDefinition valueNode)
		{
			// Verify if a custom markup extension exists
			return valueNode
				.Objects
				.Any(o => IsCustomMarkupExtensionType(o.Type));
		}

		private bool HasBindingMarkupExtension(XamlMemberDefinition valueNode)
		{
			return valueNode
				.Objects
				.Any(o =>
					o.Type.Name == "Binding"
					|| o.Type.Name == "Bind"
					|| o.Type.Name == "TemplateBinding"
				);
		}

		private bool IsCustomMarkupExtensionType(XamlType xamlType)
		{
			if (xamlType == null)
			{
				return false;
			}

			// Adjustment for Uno.Xaml parser which returns the namespace in the name
			var xamlTypeName = xamlType.Name.Contains(':') ? xamlType.Name.Split(':').LastOrDefault() : xamlType.Name;

			// Determine if the type is a custom markup extension
			return _markupExtensionTypes.Any(ns => ns.Name.Equals(xamlTypeName, StringComparison.InvariantCulture));
		}

		private XamlMemberDefinition FindMember(XamlObjectDefinition xamlObjectDefinition, string memberName)
		{
			return xamlObjectDefinition.Members.FirstOrDefault(m => m.Member.Name == memberName);
		}

		private XamlMemberDefinition GetMember(XamlObjectDefinition xamlObjectDefinition, string memberName)
		{
			var member = FindMember(xamlObjectDefinition, memberName);

			if (member == null)
			{
				throw new InvalidOperationException($"Unable to find {memberName} on {xamlObjectDefinition}");
			}

			return member;
		}

		private (string ns, string className) GetClassName(XamlObjectDefinition control)
		{
			var classMember = FindClassName(control);

			if (classMember.ns == null)
			{
				throw new Exception("Unable to find class name for toplevel control");
			}

			return classMember;
		}

		private static (string ns, string className) FindClassName(XamlObjectDefinition control)
		{
			var classMember = control.Members.FirstOrDefault(m => m.Member.Name == "Class");

			if (classMember != null)
			{
				var fullName = classMember.Value.ToString();

				var index = fullName.LastIndexOf('.');

				return (fullName.Substring(0, index), fullName.Substring(index + 1));
			}
			else
			{
				return (null, null);
			}
		}

		private bool BuildProperties(IIndentedStringBuilder writer, XamlObjectDefinition topLevelControl, bool isInline = true, bool returnsContent = false, string closureName = null)
		{
			try
			{
				BuildSourceLineInfo(writer, topLevelControl);

				if (topLevelControl.Members.Any())
				{
					var setterPrefix = string.IsNullOrWhiteSpace(closureName) ? string.Empty : closureName + ".";

					var implicitContentChild = FindImplicitContentMember(topLevelControl);

					if (implicitContentChild != null)
					{
						if (IsTextBlock(topLevelControl.Type))
						{
							var objectUid = GetObjectUid(topLevelControl);
							var isLocalized = objectUid != null &&
								BuildLocalizedResourceValue(null, "Text", objectUid) != null;

							if (implicitContentChild.Objects.Count != 0 &&
								// A localized value is available. Ignore this implicit content as localized resources take precedence over XAML.
								!isLocalized)
							{
								using (writer.BlockInvariant("{0}Inlines = ", setterPrefix))
								{
									foreach (var child in implicitContentChild.Objects)
									{
										BuildChild(writer, implicitContentChild, child);
										writer.AppendLineInvariant(",");
									}
								}
							}
							else if (implicitContentChild.Value != null)
							{
								var escapedString = DoubleEscape(implicitContentChild.Value.ToString());
								writer.AppendFormatInvariant($"{setterPrefix}Text = \"{escapedString}\"");
							}
						}
						else if (IsRun(topLevelControl.Type))
						{
							if (implicitContentChild.Value != null)
							{
								var escapedString = DoubleEscape(implicitContentChild.Value.ToString());
								writer.AppendFormatInvariant($"{setterPrefix}Text = \"{escapedString}\"");
							}
						}
						else if (IsSpan(topLevelControl.Type))
						{
							if (implicitContentChild.Objects.Count != 0)
							{
								using (writer.BlockInvariant("{0}Inlines = ", setterPrefix))
								{
									foreach (var child in implicitContentChild.Objects)
									{
										BuildChild(writer, implicitContentChild, child);
										writer.AppendLineInvariant(",");
									}
								}
							}
							else if (implicitContentChild.Value != null)
							{
								using (writer.BlockInvariant("{0}Inlines = ", setterPrefix))
								{
									var escapedString = DoubleEscape(implicitContentChild.Value.ToString());
									writer.AppendLine("new Run { Text = \"" + escapedString + "\" }");
								}
							}
						}
						else if (IsUserControl(topLevelControl.Type))
						{
							if (implicitContentChild.Objects.Any())
							{
								var firstChild = implicitContentChild.Objects.First();

								var elementType = FindType(topLevelControl.Type);
								var contentProperty = FindContentProperty(elementType);

								writer.AppendFormatInvariant(returnsContent ? "{0} content = " : "{1}{2} = ",
									GetType(firstChild.Type).ToDisplayString(),
									setterPrefix,
									contentProperty != null ? contentProperty.Name : "Content"
								);

								BuildChild(writer, implicitContentChild, firstChild);
							}
						}
						else if (IsPage(topLevelControl.Type))
						{
							if (implicitContentChild.Objects.Any())
							{
								writer.AppendFormatInvariant("{0}Content = ", setterPrefix);

								BuildChild(writer, implicitContentChild, implicitContentChild.Objects.First());
							}
						}
						else if (IsBorder(topLevelControl.Type))
						{
							if (implicitContentChild.Objects.Any())
							{
								if (implicitContentChild.Objects.Count > 1)
								{
									throw new InvalidOperationException("The type {0} does not support multiple children.".InvariantCultureFormat(topLevelControl.Type.Name));
								}

								writer.AppendFormatInvariant("{0}Child = ", setterPrefix);

								BuildChild(writer, implicitContentChild, implicitContentChild.Objects.First());
							}
						}
						else if (IsLinearGradientBrush(topLevelControl.Type))
						{
							BuildCollection(writer, isInline, setterPrefix + "GradientStops", implicitContentChild);
						}
						else if (IsInitializableCollection(topLevelControl))
						{
							var elementType = FindType(topLevelControl.Type);

							if (elementType != null)
							{
								if (IsDictionary(elementType))
								{
									foreach (var child in implicitContentChild.Objects)
									{
										if (GetMember(child, "Key") is var keyDefinition)
										{
											using (writer.BlockInvariant(""))
											{
												writer.AppendLineInvariant($"\"{keyDefinition.Value}\"");
												writer.AppendLineInvariant(",");
												BuildChild(writer, implicitContentChild, child);
											}
										}
										else
										{
											GenerateError(writer, "Unable to find the x:Key property");
										}

										writer.AppendLineInvariant(",");
									}
								}
								else
								{
									foreach (var child in implicitContentChild.Objects)
									{
										BuildChild(writer, implicitContentChild, child);
										writer.AppendLineInvariant($", /* IsInitializableCollection {elementType} */");
									}
								}
							}
						}
						else
						{
							var elementType = FindType(topLevelControl.Type);

							if (elementType != null)
							{
								var contentProperty = FindContentProperty(elementType);

								if (contentProperty != null)
								{
									if (IsCollectionOrListType(contentProperty.Type as INamedTypeSymbol))
									{
										string newableTypeName;

										if (IsInitializableProperty(contentProperty))
										{
											if (isInline)
											{
												using (writer.BlockInvariant(contentProperty.Name + " = "))
												{
													foreach (var child in implicitContentChild.Objects)
													{
														BuildChild(writer, implicitContentChild, child);
														writer.AppendLineInvariant(",");
													}
												}
											}
											else
											{
												foreach (var inner in implicitContentChild.Objects)
												{
													writer.AppendLine($"{contentProperty.Name}.Add(");

													BuildChild(writer, implicitContentChild, inner);

													writer.AppendLineInvariant(");");
												}
											}
										}
										else if (IsNewableProperty(contentProperty, out newableTypeName))
										{
											if (string.IsNullOrWhiteSpace(newableTypeName))
											{
												throw new InvalidOperationException("Unable to initialize newable collection type.  Type name for property {0} is empty."
													.InvariantCultureFormat(contentProperty.Name));
											}

											// Explicitly instantiate the collection and set it using the content property
											if (implicitContentChild.Objects.Count == 1
												&& contentProperty.Type == FindType(implicitContentChild.Objects[0].Type))
											{
												writer.AppendFormatInvariant(contentProperty.Name + " = ");
												BuildChild(writer, implicitContentChild, implicitContentChild.Objects[0]);
												writer.AppendLineInvariant(",");
											}
											else
											{
												using (writer.BlockInvariant("{0}{1} = new {2} ", setterPrefix, contentProperty.Name, newableTypeName))
												{
													foreach (var child in implicitContentChild.Objects)
													{
														BuildChild(writer, implicitContentChild, child);
														writer.AppendLineInvariant(",");
													}
												}
											}
										}
										else if (GetKnownNewableListOrCollectionInterface(contentProperty as INamedTypeSymbol, out newableTypeName))
										{
											if (string.IsNullOrWhiteSpace(newableTypeName))
											{
												throw new InvalidOperationException("Unable to initialize known newable collection or list interface.  Type name for property {0} is empty."
													.InvariantCultureFormat(contentProperty.Name));
											}

											using (writer.BlockInvariant("{0}{1} = new {2} ", setterPrefix, contentProperty.Name, newableTypeName))
											{
												foreach (var child in implicitContentChild.Objects)
												{
													BuildChild(writer, implicitContentChild, child);
													writer.AppendLineInvariant(",");
												}
											}
										}
										else
										{
											throw new InvalidOperationException("There is no known way to initialize collection property {0}."
												.InvariantCultureFormat(contentProperty.Name));
										}
									}
									else
									{
										var objectUid = GetObjectUid(topLevelControl);
										var isLocalized = objectUid != null &&
											IsLocalizablePropertyType(contentProperty.Type as INamedTypeSymbol) &&
											BuildLocalizedResourceValue(null, contentProperty.Name, objectUid) != null;

										if (implicitContentChild.Objects.Any() &&
											// A localized value is available. Ignore this implicit content as localized resources take precedence over XAML.
											!isLocalized)
										{
											writer.AppendLineInvariant(setterPrefix + contentProperty.Name + " = ");

											if (implicitContentChild.Objects.Count > 1)
											{
												throw new InvalidOperationException("The type {0} does not support multiple children.".InvariantCultureFormat(topLevelControl.Type.Name));
											}

											BuildChild(writer, implicitContentChild, implicitContentChild.Objects.First());

											if (isInline)
											{
												writer.AppendLineInvariant(",");
											}
										}
										else if (
											implicitContentChild.Value is string implicitValue
											&& implicitValue.HasValueTrimmed()
										)
										{
											writer.AppendLineInvariant(setterPrefix + contentProperty.Name + " = \"" + implicitValue + "\"");

											if (isInline)
											{
												writer.AppendLineInvariant(",");
											}
										}
									}
								}
							}
						}

						return true;
					}
					else if (returnsContent && IsUserControl(topLevelControl.Type))
					{
						writer.AppendFormatInvariant(XamlConstants.Types.IFrameworkElement + " content = null");
					}
				}

				return false;
			}
			catch (Exception e)
			{
				throw new InvalidOperationException(
					"An error occurred when processing {0} at line {1}:{2} ({3}) : {4}"
					.InvariantCultureFormat(
						topLevelControl.Type.Name,
						topLevelControl.LineNumber,
						topLevelControl.LinePosition,
						_fileDefinition.FilePath,
						e.Message
					)
					, e
				);
			}
		}

		private static IPropertySymbol FindContentProperty(INamedTypeSymbol elementType)
		{
			return _findContentProperty(elementType);
		}

		private static IPropertySymbol SourceFindContentProperty(INamedTypeSymbol elementType)
		{
			var data = elementType
				.GetAllAttributes()
				.FirstOrDefault(t => t.AttributeClass.ToDisplayString() == XamlConstants.Types.ContentPropertyAttribute);

			if (data != null)
			{
				var nameProperty = data.NamedArguments.Where(f => f.Key == "Name").FirstOrDefault();

				if (nameProperty.Value.Value != null)
				{
					var name = nameProperty.Value.Value.ToString();

					return elementType?.GetAllProperties().FirstOrDefault(p => p.Name == name);
				}
			}

			return null;
		}

		private string GetFullGenericTypeName(INamedTypeSymbol propertyType)
		{
			if (propertyType == null)
			{
				return string.Empty;
			}

			return "{0}.{1}".InvariantCultureFormat(propertyType.ContainingNamespace, propertyType.Name) +
				(
					propertyType.IsGenericType ?
					"<{0}>".InvariantCultureFormat(propertyType.TypeArguments.Select(ts => GetFullGenericTypeName(ts as INamedTypeSymbol)).JoinBy(",")) :
					string.Empty
				);
		}

		private void BuildCollection(IIndentedStringBuilder writer, bool isInline, string name, XamlMemberDefinition implicitContentChild)
		{
			if (isInline)
			{
				using (writer.BlockInvariant(name + " = "))
				{
					foreach (var child in implicitContentChild.Objects)
					{
						BuildChild(writer, implicitContentChild, child);
						writer.AppendLineInvariant(",");
					}
				}
			}
			else
			{
				foreach (var child in implicitContentChild.Objects)
				{
					writer.AppendLineInvariant(name + ".Add(");
					BuildChild(writer, implicitContentChild, child);
					writer.AppendLineInvariant(");");
				}
			}
		}

		private bool IsPage(XamlType xamlType) => IsType(xamlType, XamlConstants.Types.NativePage);

		private bool IsApplication(XamlType xamlType) => IsType(xamlType, XamlConstants.Types.Application);

		private XamlMemberDefinition FindImplicitContentMember(XamlObjectDefinition topLevelControl, string memberName = "_UnknownContent")
		{
			return topLevelControl
				.Members
				.FirstOrDefault(m => m.Member.Name == memberName);
		}

		/// <summary>
		/// Processes a Resources node, creating a ResourceDictionary if needed, and saving static resources for later registration.
		/// </summary>
		/// <param name="isInInitializer">True if inside an object initialization</param>
		private void RegisterAndBuildResources(IIndentedStringBuilder writer, XamlObjectDefinition topLevelControl, bool isInInitializer)
		{
			var resourcesMember = topLevelControl.Members.FirstOrDefault(m => m.Member.Name == "Resources");

			if (resourcesMember != null)
			{
				// To be able to have MergedDictionaries, the first node of the Resource node
				// must be an explicit resource dictionary.
				var isExplicitResDictionary = resourcesMember.Objects.Any(o => o.Type.Name == "ResourceDictionary");
				var resourcesRoot = isExplicitResDictionary
					? FindImplicitContentMember(resourcesMember.Objects.First())
					: resourcesMember;
				var mergedDictionaries = isExplicitResDictionary ?
					resourcesMember.Objects
						.First().Members
							.Where(m => m.Member.Name == "MergedDictionaries")
							.FirstOrDefault()
					: null;
				var themeDictionaries = isExplicitResDictionary ?
					resourcesMember.Objects
						.First().Members
							.Where(m => m.Member.Name == "ThemeDictionaries")
							.FirstOrDefault()
					: null;

				if (resourcesRoot != null || mergedDictionaries != null)
				{
					if (isInInitializer)
					{
						writer.AppendLineInvariant("Resources = {{");
					}

					BuildResourceDictionary(writer, resourcesRoot, isInInitializer);
					BuildMergedDictionaries(writer, mergedDictionaries, isInInitializer);
					BuildThemeDictionaries(writer, themeDictionaries, isInInitializer);

					if (isInInitializer)
					{
						writer.AppendLineInvariant("}},");
					}
				}
			}
		}

		private void BuildResourceDictionary(IIndentedStringBuilder writer, XamlMemberDefinition resourcesRoot, bool isInInitializer)
		{
			var closingPunctuation = isInInitializer ? "," : ";";

			foreach (var resource in (resourcesRoot?.Objects).Safe())
			{
				var key = GetDictionaryResourceKey(resource, out var name);

				if (resource.Type.Name == "StaticResource")
				{
					writer.AppendLineInvariant("// StaticResource in ResourceDictionary not supported."); //TODO: revisit this when StaticResource resolution is in
					continue;
				}

				if (key != null)
				{
					var wrappedKey = key;
					if (!key.StartsWith("typeof(", StringComparison.InvariantCulture))
					{
						wrappedKey = "\"{0}\"".InvariantCultureFormat(key);
					}

					if (isInInitializer)
					{
						writer.AppendLineInvariant("[{0}] = ", wrappedKey);
					}
					else
					{
						writer.AppendLineInvariant("Resources[{0}] = ", wrappedKey);
					}
					var directproperty = GetResourceDictionaryPropertyName(key);
					if (directproperty != null)
					{
						writer.AppendLineInvariant(directproperty);
					}
					else
					{
						BuildChild(writer, null, resource);
					}
					writer.AppendLineInvariant(closingPunctuation);
				}

				if (name != null)
				{
					_namedResources.Add(name, resource);
				}
			}
		}

		/// <summary>
		/// Populate MergedDictionaries property of a ResourceDictionary.
		/// </summary>
		private void BuildMergedDictionaries(IIndentedStringBuilder writer, XamlMemberDefinition mergedDictionaries, bool isInInitializer)
		{
			var propertyName = "MergedDictionaries";
			BuildDictionaryCollection(writer, mergedDictionaries, isInInitializer, propertyName, isDict: false);
		}

		/// <summary>
		/// Populate ThemeDictionaries property of a ResourceDictionary.
		/// </summary>
		private void BuildThemeDictionaries(IIndentedStringBuilder writer, XamlMemberDefinition themeDictionaries, bool isInInitializer)
		{
			BuildDictionaryCollection(writer, themeDictionaries, isInInitializer, "ThemeDictionaries", isDict: true);
		}

		/// <summary>
		/// Build a collection of ResourceDictionaries.
		/// </summary>
		private void BuildDictionaryCollection(IIndentedStringBuilder writer, XamlMemberDefinition mergedDictionaries, bool isInInitializer, string propertyName, bool isDict)
		{
			if (mergedDictionaries == null)
			{
				return;
			}

			if (isInInitializer)
			{
				writer.AppendLineInvariant("{0} = {{", propertyName);
			}
			else
			{
				writer.AppendLineInvariant("// {0}", propertyName);
			}
			foreach (var dictObject in mergedDictionaries.Objects)
			{
				if (dictObject.Members.Count == 0)
				{
					continue;
				}

				var source = dictObject.Members.FirstOrDefault(m => m.Member.Name == "Source");
				if (source != null && dictObject.Members.Any(m => m.Member.Name == "_UnknownContent"))
				{
					throw new Exception("Local values are not allowed in resource dictionary with Source set");
				}

				var key = GetDictionaryResourceKey(dictObject);
				if (isDict && key == null)
				{
					throw new Exception("Each dictionary entry must have an associated key.");
				}

				var former = _themeDictionaryCurrentlyBuilding; //Will 99% of the time be null. 
				if (isDict)
				{
					_themeDictionaryCurrentlyBuilding = key;
				}

				if (!isInInitializer && !isDict)
				{
					writer.AppendLineInvariant("Resources.{0}.Add(", propertyName);
				}
				else if (!isInInitializer && isDict)
				{
					writer.AppendLineInvariant("Resources.{0}[\"{1}\"] = ", propertyName, key);
				}
				else if (isInInitializer && isDict)
				{
					writer.AppendLineInvariant("[\"{0}\"] = ", key);
				}

				if (source != null)
				{
					BuildDictionaryFromSource(writer, source, isInInitializer);
				}
				else
				{
					InitializeAndBuildResourceDictionary(writer, dictObject);
				}
				if (isInInitializer)
				{
					writer.AppendLineInvariant(",");
				}
				else if (!isDict)
				{
					writer.AppendLineInvariant(");");
				}
				else
				{
					writer.AppendLineInvariant(";");
				}

				_themeDictionaryCurrentlyBuilding = former;
			}

			if (isInInitializer)
			{
				writer.AppendLineInvariant("}},");
			}
		}

		/// <summary>
		/// Try to create a ResourceDictionary assignment from supplied Source property.
		/// </summary>
		private void BuildDictionaryFromSource(IIndentedStringBuilder writer, XamlMemberDefinition sourceDef, bool isInInitializer)
		{
			var source = (sourceDef?.Value as string)?.Replace('\\', '/');
			if (source == null)
			{
				return;
			}
			var sourceProp = _globalStaticResourcesMap.FindTargetPropertyForMergedDictionarySource(_fileDefinition, source);
			writer.AppendLineInvariant("global::{0}.GlobalStaticResources.{1}", _defaultNamespace, sourceProp);
		}

		private bool IsTextBlock(XamlType xamlType)
		{
			return IsType(xamlType, XamlConstants.Types.TextBlock);
		}

		private void TryExtractAutomationId(XamlMemberDefinition member, string[] targetMembers, ref string uiAutomationId)
		{
			if (uiAutomationId.HasValue())
			{
				return;
			}

			var fullMemberName = $"{FindType(member.Member.DeclaringType)?.GetFullMetadataName()}.{member.Member.Name}";

			// Try to match each potential candidate by comparing based only on the name of the member first.
			// If that fails, try matching based on the full metadata name of the member
			var hasUiAutomationMapping = targetMembers
				.Any(candidateMember =>
					(!candidateMember.Contains(".") && candidateMember == member.Member.Name) ||
					candidateMember == fullMemberName
				);

			if (!hasUiAutomationMapping)
			{
				return;
			}

			var bindingMembers = member
				.Objects
				.FirstOrDefault(o => o.Type.Name == "Binding" || o.Type.Name == "TemplateBinding")
				?.Members
				.ToArray();

			string bindingPath;

			// Checks the first binding member, which can be used to implicitlty declare the binding path (i.e. without
			// declaring a "Path=" specifier). Otherwise, the we look for any explicit binding path declaration.
			var firstBindingMember = bindingMembers?.FirstOrDefault();
			if (firstBindingMember != null &&
				(firstBindingMember.Member.Name == "Path" ||
				 firstBindingMember.Member.Name == "_PositionalParameters" ||
				 firstBindingMember.Member.Name.IsNullOrWhiteSpace())
			)
			{
				bindingPath = firstBindingMember.Value.ToString();
			}
			else
			{
				bindingPath = bindingMembers
					?.FirstOrDefault(bindingMember => bindingMember.Member.Name == "Path")
					?.Value
					?.ToString();
			}

			if (bindingPath.IsNullOrEmpty())
			{
				return;
			}

			// Replace bracket-based notation with dot notation.
			// ex: [FirstMember][SecondMember] --> FirstMember.SecondMember
			uiAutomationId = new string(bindingPath
				.TrimStart("Parent")
				.Replace("][", ".")
				.Where(c => c != '[' && c != ']')
				.ToArray());
		}

		private void BuildExtendedProperties(IIndentedStringBuilder outerwriter, XamlObjectDefinition objectDefinition, bool useChildTypeForNamedElement = false, bool useGenericApply = false)
		{
			var objectUid = GetObjectUid(objectDefinition);

			var extendedProperties = GetExtendedProperties(objectDefinition);
			bool hasChildrenWithPhase = HasChildrenWithPhase(objectDefinition);
			var isFrameworkElement = IsFrameworkElement(objectDefinition.Type);

			if (extendedProperties.Any() || hasChildrenWithPhase || isFrameworkElement)
			{
				string closureName;

				using (var writer = CreateApplyBlock(outerwriter, useGenericApply ? null : GetType(objectDefinition.Type), out closureName))
				{
					XamlMemberDefinition uidMember = null;
					XamlMemberDefinition nameMember = null;
					string uiAutomationId = null;
					string[] extractionTargetMembers = null;

					if (_isUiAutomationMappingEnabled)
					{
						// Look up any potential UI automation member mappings available for the current object definition
						extractionTargetMembers = _uiAutomationMappings
							?.FirstOrDefault(m => IsType(objectDefinition.Type, m.Key) || IsImplementingInterface(FindType(objectDefinition.Type), FindType(m.Key)))
							.Value
							?.ToArray() ?? new string[0];
					}

					if (hasChildrenWithPhase)
					{
						writer.AppendLine(GenerateRootPhases(objectDefinition, closureName));
					}

					foreach (var member in extendedProperties)
					{
						if (extractionTargetMembers != null)
						{
							TryExtractAutomationId(member, extractionTargetMembers, ref uiAutomationId);
						}

						if (HasMarkupExtension(member))
						{
							TryValidateContentPresenterBinding(writer, objectDefinition, member);

							BuildComplexPropertyValue(writer, member, closureName + ".", closureName);
						}
						else if (HasCustomMarkupExtension(member))
						{
							if (IsAttachedProperty(member) && FindPropertyType(member.Member) != null)
							{
								BuildSetAttachedProperty(writer, closureName, member, objectUid, isCustomMarkupExtension: true);
							}
							else
							{
								BuildCustomMarkupExtensionPropertyValue(writer, member, closureName + ".");
							}
						}
						else if (member.Objects.Any())
						{
							if (member.Member.Name == "_UnknownContent") // So : FindType(member.Owner.Type) is INamedTypeSymbol type && IsCollectionOrListType(type)
							{
								foreach (var item in member.Objects)
								{
									writer.AppendLineInvariant($"{closureName}.Add(");
									using (writer.Indent())
									{
										BuildChild(writer, member, item);
									}
									writer.AppendLineInvariant(");");
								}
							}
							else if (!IsType(objectDefinition.Type, member.Member.DeclaringType))
							{
								var ownerType = GetType(member.Member.DeclaringType);

								var propertyType = GetPropertyType(member.Member);

								if (IsExactlyCollectionOrListType(propertyType))
								{
									// If the property is specifically an IList or an ICollection
									// we can use C#'s collection initializer.
									writer.AppendLineInvariant(
										"{0}.Set{1}({2}, ",
										GetGlobalizedTypeName(ownerType.ToDisplayString()),
										member.Member.Name,
										closureName
									);

									using (writer.BlockInvariant("new[]"))
									{
										foreach (var inner in member.Objects)
										{
											BuildChild(writer, member, inner);
											writer.AppendLine(",");
										}
									}

									writer.AppendLineInvariant(");");
								}
								else if (IsCollectionOrListType(propertyType))
								{
									// If the property is a concrete type that implements an IList or
									// an ICollection, we must get the property and call add explicitly
									// on it.
									var localCollectionName = $"{closureName}_collection_{_collectionIndex++}";

									writer.AppendLineInvariant(
										$"var {localCollectionName} = {ownerType.ToDisplayString()}.Get{member.Member.Name}({closureName});"
									);

									foreach (var inner in member.Objects)
									{
										writer.AppendLine($"{localCollectionName}.Add(");

										BuildChild(writer, member, inner);

										writer.AppendLineInvariant(");");
									}
								}
								else
								{
									// If the property is specifically an IList or an ICollection
									// we can use C#'s collection initializer.
									writer.AppendLineInvariant(
										"{0}.Set{1}({2}, ",
										GetGlobalizedTypeName(ownerType.ToDisplayString()),
										member.Member.Name,
										closureName
									);

									if (member.Objects.Count() == 1)
									{
										BuildChild(writer, member, member.Objects.First());
									}
									else
									{
										throw new InvalidOperationException($"The property {member.Member.Name} of type {propertyType} does not support adding multiple objects.");
									}

									writer.AppendLineInvariant(");");
								}
							}
							else
							{
								// GenerateWarning(writer, $"Unknown type {objectDefinition.Type} for property {member.Member.DeclaringType}");
							}
						}
						else
						{
							if (
								member.Member.Name == "Name"
								&& !IsMemberInsideResourceDictionary(objectDefinition)
							)
							{
								writer.AppendLineInvariant($@"nameScope.RegisterName(""{member.Value}"", {closureName});");
							}

							if (
								member.Member.Name == "Name"
								&& !IsAttachedProperty(member)
								&& !IsMemberInsideResourceDictionary(objectDefinition)
							)
							{
								nameMember = member;
								var value = member.Value.ToString();
								var type = useChildTypeForNamedElement ?
									GetImplicitChildTypeDisplayString(objectDefinition) :
									FindType(objectDefinition.Type)?.ToDisplayString();

								if (type == null)
								{
									throw new InvalidOperationException($"Unable to find type {objectDefinition.Type}");
								}

								writer.AppendLineInvariant("this.{0} = {1};", value, closureName);
								RegisterBackingField(type, value, FindObjectFieldAccessibility(objectDefinition));
							}
							else if (member.Member.Name == "Name"
								&& member.Member.PreferredXamlNamespace == XamlConstants.XamlXmlNamespace)
							{
								writer.AppendLineInvariant("// x:Name {0}", member.Value, member.Value);
							}
							else if (member.Member.Name == "Key")
							{
								writer.AppendLineInvariant("// Key {0}", member.Value, member.Value);
							}
							else if (member.Member.Name == "DeferLoadStrategy"
								&& member.Member.PreferredXamlNamespace == XamlConstants.XamlXmlNamespace)
							{
								writer.AppendLineInvariant("// DeferLoadStrategy {0}", member.Value);
							}
							else if (member.Member.Name == "Load"
								&& member.Member.PreferredXamlNamespace == XamlConstants.XamlXmlNamespace)
							{
								writer.AppendLineInvariant("// Load {0}", member.Value);
							}
							else if (member.Member.Name == "Uid")
							{
								uidMember = member;
								writer.AppendLineInvariant($"{GlobalPrefix}Uno.UI.Helpers.MarkupHelper.SetXUid({closureName}, \"{objectUid}\");");
							}
							else if (member.Member.Name == "FieldModifier")
							{
								writer.AppendLineInvariant("// FieldModifier {0}", member.Value);
							}
							else if (member.Member.Name == "Phase")
							{
								writer.AppendLineInvariant($"{GlobalPrefix}Uno.UI.FrameworkElementHelper.SetRenderPhase({closureName}, {member.Value});");
							}
							else if (member.Member.Name == "Class" && member.Member.PreferredXamlNamespace == XamlConstants.XamlXmlNamespace)
							{
								writer.AppendLineInvariant("// Class {0}", member.Value, member.Value);
							}
							else if (
								member.Member.Name == "TargetName" &&
								IsAttachedProperty(member) &&
								member.Member.DeclaringType?.Name == "Storyboard"
							)
							{
								writer.AppendLineInvariant(@"{0}.SetTargetName({2}, ""{1}"");",
									GetGlobalizedTypeName(FindType(member.Member.DeclaringType).SelectOrDefault(t => t.ToDisplayString(), member.Member.DeclaringType.Name)),
									this.RewriteAttachedPropertyPath(member.Value.ToString()),
									closureName);

								writer.AppendLineInvariant("{0}.SetTarget({2}, _{1}Subject);",
												GetGlobalizedTypeName(FindType(member.Member.DeclaringType).SelectOrDefault(t => t.ToDisplayString(), member.Member.DeclaringType.Name)),
												member.Value,
												closureName);
							}
							else if (
								member.Member.Name == "TargetProperty" &&
								IsAttachedProperty(member) &&
								member.Member.DeclaringType?.Name == "Storyboard"
							)
							{
								writer.AppendLineInvariant(@"{0}.SetTargetProperty({2}, ""{1}"");",
									GetGlobalizedTypeName(FindType(member.Member.DeclaringType).SelectOrDefault(t => t.ToDisplayString(), member.Member.DeclaringType.Name)),
									this.RewriteAttachedPropertyPath(member.Value.ToString()),
									closureName);
							}
							else if (
								member.Member.DeclaringType?.Name == "RelativePanel" &&
								IsAttachedProperty(member) &&
								IsRelativePanelSiblingProperty(member.Member.Name)
							)
							{
								writer.AppendLineInvariant(@"{0}.Set{1}({2}, _{3}Subject);",
									GetGlobalizedTypeName(FindType(member.Member.DeclaringType).SelectOrDefault(t => t.ToDisplayString(), member.Member.DeclaringType.Name)),
									member.Member.Name,
									closureName,
									member.Value);
							}
							else
							{
								IEventSymbol eventSymbol = null;

								if (
									!IsType(member.Member.DeclaringType, objectDefinition.Type)
									|| IsAttachedProperty(member)
									|| (eventSymbol = FindEventType(member.Member)) != null
								)
								{
									if (FindPropertyType(member.Member) != null)
									{
										BuildSetAttachedProperty(writer, closureName, member, objectUid, isCustomMarkupExtension: false);
									}
									else if (eventSymbol != null)
									{
										// If a binding is inside a DataTemplate, the binding root in the case of an x:Bind is
										// the DataContext, not the control's instance.
										var isInsideDataTemplate = IsMemberInsideFrameworkTemplate(member.Owner);

										void writeEvent(string ownerPrefix)
										{
											if (eventSymbol.Type is INamedTypeSymbol delegateSymbol)
											{
												var parms = delegateSymbol
													.DelegateInvokeMethod
													.Parameters
													.Select(p => member.Value + "_" + p.Name)
													.JoinBy(",");

												var eventSource = ownerPrefix.HasValue() ? ownerPrefix : "this";

												//
												// Generate a weak delegate, so the owner is not being held onto by the delegate. We can
												// use the WeakReferenceProvider to get a self reference to avoid adding the cost of the
												// creation of a WeakReference.
												//
												writer.AppendLineInvariant($"var {member.Member.Name}_{member.Value}_That = ({eventSource} as global::Uno.UI.DataBinding.IWeakReferenceProvider).WeakReference;");
												writer.AppendLineInvariant($"{closureName}.{member.Member.Name} += ({parms}) => ({member.Member.Name}_{member.Value}_That.Target as {_className.className})?.{member.Value}({parms});");
											}
											else
											{
												GenerateError(writer, $"{eventSymbol.Type} is not a supported event");
											}
										}

										if (!isInsideDataTemplate)
										{
											writeEvent("");
										}
										else if (_className.className != null)
										{
											_hasLiteralEventsRegistration = true;
											writer.AppendLineInvariant($"{closureName}.RegisterPropertyChangedCallback(");
											using (writer.BlockInvariant($"global::Uno.UI.Xaml.XamlInfo.XamlInfoProperty, (s, p) =>"))
											{
												using (writer.BlockInvariant($"if (global::Uno.UI.Xaml.XamlInfo.GetXamlInfo({closureName})?.Owner is {_className.className} owner)"))
												{
													writeEvent("owner");
												}
											}
											writer.AppendLineInvariant($");");
										}
										else
										{
											GenerateError(writer, $"Unable to use event {member.Member.Name} without a backing class (use x:Class)");
										}
									}
									else
									{
										GenerateError(
											writer,
											$"Property {member.Member.PreferredXamlNamespace}:{member.Member} is not available on {member.Member.DeclaringType?.Name}, value is {member.Value}"
										);
									}
								}
							}
						}
					}

					if (_isUiAutomationMappingEnabled)
					{
						// Prefer using the Uid or the Name if their value has been explicitly assigned
						var assignedUid = uidMember?.Value?.ToString();
						var assignedName = nameMember?.Value?.ToString();

						if (assignedUid.HasValue())
						{
							uiAutomationId = assignedUid;
						}
						else if (assignedName.HasValue())
						{
							uiAutomationId = assignedName;
						}

						BuildUiAutomationId(writer, closureName, uiAutomationId, objectDefinition);
					}

					if (isFrameworkElement)
					{
						// This should always be the last thing called when an element is parsed.
						writer.AppendLineInvariant("{0}.CreationComplete();", closureName);
					}
				}
			}

			// Local function used to build a property/value for any custom MarkupExtensions
			void BuildCustomMarkupExtensionPropertyValue(IIndentedStringBuilder writer, XamlMemberDefinition member, string prefix)
			{
				Func<string, string> formatLine = format => prefix + format + (prefix.HasValue() ? ";\r\n" : "");

				var propertyValue = GetCustomMarkupExtensionValue(member);

				if (propertyValue.HasValue())
				{
					var formatted = formatLine($"{member.Member.Name} = {propertyValue}");

					writer.AppendLine(formatted);
				}
			}
		}

		/// <summary>
		/// Build localized properties which have not been set in the xaml.
		/// </summary>
		private void BuildLocalizedProperties(IIndentedStringBuilder writer, XamlObjectDefinition objectDefinition)
		{
			var objectUid = GetObjectUid(objectDefinition);

			if (objectUid != null)
			{
				var candidateProperties = FindLocalizableProperties(objectDefinition.Type)
					.Except(objectDefinition.Members.Select(m => m.Member.Name));
				foreach (var prop in candidateProperties)
				{
					var localizedValue = BuildLocalizedResourceValue(null, prop, objectUid);
					if (localizedValue != null)
					{
						writer.AppendLineInvariant("{0} = {1},", prop, localizedValue);
					}
				}
			}
		}

		private void TryValidateContentPresenterBinding(IIndentedStringBuilder writer, XamlObjectDefinition objectDefinition, XamlMemberDefinition member)
		{
			if (
				FindType(objectDefinition.Type) == _contentPresenterSymbol
				&& member.Member.Name == "Content"
			)
			{
				var binding = member.Objects.FirstOrDefault(o => o.Type.Name == "Binding");

				if (binding != null)
				{
					var hasRelativeSource = binding.Members
						.Any(m =>
							m.Member.Name == "RelativeSource"
						// It can either be TemplatedParent or Self. In either cases, it does not use the inherited
						// DataContext, which falls outside of the scenario we want to avoid.
						);

					if (!hasRelativeSource)
					{
						writer.AppendFormatInvariant(
							"\n#error Using a non-template binding expression on Content " +
							"will likely result in an undefined runtime behavior, as ContentPresenter.Content overrides " +
							"the local value of ContentPresenter.DataContent. " +
							"Use ContentControl instead if you really need a normal binding on Content.\n");
					}
				}
			}
		}

		private void BuildUiAutomationId(IIndentedStringBuilder writer, string closureName, string uiAutomationId, XamlObjectDefinition parent)
		{
			if (uiAutomationId.IsNullOrEmpty())
			{
				return;
			}

			writer.AppendLineInvariant("// UI automation id: {0}", uiAutomationId);

			// ContentDescription and AccessibilityIdentifier are used by Xamarin.UITest (Test Cloud) to identify visual elements
			if (IsAndroidView(parent.Type))
			{
				writer.AppendLineInvariant("{0}.ContentDescription = \"{1}\";", closureName, uiAutomationId);
			};

			if (IsIOSUIView(parent.Type))
			{
				writer.AppendLineInvariant("{0}.AccessibilityIdentifier = \"{1}\";", closureName, uiAutomationId);
			}
		}

		private bool IsRelativePanelSiblingProperty(string name)
		{
			return name.Equals("Above") ||
				name.Equals("Below") ||
				name.Equals("LeftOf") ||
				name.Equals("RightOf") ||
				name.Equals("AlignBottomWith") ||
				name.Equals("AlignHorizontalCenterWith") ||
				name.Equals("AlignLeftWith") ||
				name.Equals("AlignRightWith") ||
				name.Equals("AlignTopWith") ||
				name.Equals("AlignVerticalCenterWith");
		}

		private void BuildSetAttachedProperty(IIndentedStringBuilder writer, string closureName, XamlMemberDefinition member, string objectUid, bool isCustomMarkupExtension)
		{
			var literalValue = isCustomMarkupExtension
					? GetCustomMarkupExtensionValue(member)
					: BuildLiteralValue(member, owner: member, objectUid: objectUid);

			writer.AppendLineInvariant(
				"{0}.Set{1}({3}, {2});",
				GetGlobalizedTypeName(FindType(member.Member.DeclaringType).SelectOrDefault(t => t.ToDisplayString(), member.Member.DeclaringType.Name)),
				member.Member.Name,
				literalValue,
				closureName
			);
		}

		private XamlLazyApplyBlockIIndentedStringBuilder CreateApplyBlock(IIndentedStringBuilder writer, INamedTypeSymbol appliedType, out string closureName)
		{
			closureName = "c" + (_applyIndex++).ToString(CultureInfo.InvariantCulture);

			//
			// Since we're using strings to generate the code, we can't know ahead of time if
			// content will be generated only by looking at the Xaml object model.
			// For now, we only observe if the inner code has generated code, and we create
			// the apply block at that time.
			//
			string delegateType = null;

			if (appliedType != null)
			{
				int typeIndex = _xamlAppliedTypes.IndexOf(appliedType);

				if (typeIndex == -1)
				{
					_xamlAppliedTypes.Add(appliedType);
					typeIndex = _xamlAppliedTypes.Count - 1;
				}

				delegateType = $"{_fileUniqueId}XamlApplyExtensions.XamlApplyHandler{typeIndex}";
			}

			return new XamlLazyApplyBlockIIndentedStringBuilder(writer, closureName, appliedType != null ? _fileUniqueId : null, delegateType);
		}

		private void RegisterPartial(string format, params object[] values)
		{
			_partials.Add(format.InvariantCultureFormat(values));
		}

		private void RegisterBackingField(string type, string name, Accessibility accessibility)
		{
			CurrentScope.BackingFields.Add(new BackingFieldDefinition(type, name, accessibility));
		}

		private void RegisterChildSubclass(string name, XamlMemberDefinition owner, string returnType)
		{
			CurrentScope.Subclasses[name] = new Subclass { ContentOwner = owner, ReturnType = returnType };
		}

		private void BuildComplexPropertyValue(IIndentedStringBuilder writer, XamlMemberDefinition member, string prefix, string closureName = null)
		{
			Func<string, string> formatLine = format => prefix + format + (prefix.HasValue() ? ";\r\n" : "");

			var bindingNode = member.Objects.FirstOrDefault(o => o.Type.Name == "Binding");
			var bindNode = member.Objects.FirstOrDefault(o => o.Type.Name == "Bind");
			var templateBindingNode = member.Objects.FirstOrDefault(o => o.Type.Name == "TemplateBinding");

			// If a binding is inside a DataTemplate, the binding root in the case of an x:Bind is
			// the DataContext, not the control's instance.
			var isInsideDataTemplate = IsMemberInsideDataTemplate(member.Owner);

			string GetBindingOptions()
			{
				if (bindingNode != null || bindNode != null)
				{
					return (bindingNode ?? bindNode)
						.Members
						.Select(BuildMemberPropertyValue)
						.Concat(bindNode != null && !isInsideDataTemplate ? new[] { "CompiledSource = this" } : Enumerable.Empty<string>())
						.JoinBy(", ");

				}
				if (templateBindingNode != null)
				{
					return templateBindingNode
						.Members
						.Select(BuildMemberPropertyValue)
						.Concat("RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent)")
						.JoinBy(", ");
				}

				return null;
			}

			var bindingOptions = GetBindingOptions();

			if (bindingOptions != null)
			{
				var isAttachedProperty = IsDependencyProperty(member.Member);
				var isBindingType = FindPropertyType(member.Member) == _dataBindingSymbol;

				if (isAttachedProperty)
				{
					var propertyOwner = GetType(member.Member.DeclaringType);

					writer.AppendLine(formatLine($"SetBinding({GetGlobalizedTypeName(propertyOwner.ToDisplayString())}.{member.Member.Name}Property, new {XamlConstants.Types.Binding}{{ {bindingOptions} }})"));
				}
				else if (isBindingType)
				{
					writer.AppendLine(formatLine($"{member.Member.Name} = new {XamlConstants.Types.Binding}{{ {bindingOptions} }}"));
				}
				else
				{
					writer.AppendLine(formatLine($"SetBinding(\"{member.Member.Name}\", new {XamlConstants.Types.Binding}{{ {bindingOptions} }})"));
				}
			}

			(var resourceKey, var isThemeResourceExtension) = GetStaticResourceKey(member);

			if (resourceKey != null)
			{

				var directProperty = GetResourceDictionaryPropertyName(resourceKey);

				if (directProperty != null)
				{
					// Prefer direct property reference (when we are in top-level ResourceDictionary and referencing resource in same dictionary)
					writer.AppendLineInvariant(formatLine("{0} = {1}"), member.Member.Name, directProperty);

				}
				else if (IsDependencyProperty(member.Member))
				{
					// To be fully compatible with UWP here, we should check the nearest dictionary in the 'XAML scope' then outwards. For
					// StaticResource extension that would be it, for ThemeResource we'd do additional resolution at load-time.
					//
					// Instead, Uno immediately sets any Application-level value, if it exists. Then we do load-time resolution by tree-walking
					// for StaticResource *and* ThemeResource. (Note that initialize-time XAML scope resolution should be possible to implement,
					// should it turn out to be necessary.)
					var propertyOwner = GetType(member.Member.DeclaringType);
					writer.AppendLineInvariant("global::Uno.UI.ResourceResolver.ApplyResource({0}, {1}.{2}Property, \"{3}\", {4});", closureName, GetGlobalizedTypeName(propertyOwner.ToDisplayString()), member.Member.Name, resourceKey, isThemeResourceExtension ? "true" : "false");
				}
				else
				{
					// Load-time resolution isn't feasible for non-DPs, so we just set the Application-level value right away, and that's it.
					writer.AppendLineInvariant(formatLine("{0} = {1}"), member.Member.Name, GetSimpleStaticResourceRetrieval(member));
				}
			}
		}

		private string BuildMemberPropertyValue(XamlMemberDefinition m)
		{
			if (IsCustomMarkupExtensionType(m.Objects.FirstOrDefault()?.Type))
			{
				// If the member contains a custom markup extension, build the inner part first
				var propertyValue = GetCustomMarkupExtensionValue(m);
				return "{0} = {1}".InvariantCultureFormat(m.Member.Name, propertyValue);
			}
			else
			{
				return "{0} = {1}".InvariantCultureFormat(m.Member.Name == "_PositionalParameters" ? "Path" : m.Member.Name, BuildBindingOption(m, FindPropertyType(m.Member), prependCastToType: true));
			}
		}

		private string GetCustomMarkupExtensionValue(XamlMemberDefinition member)
		{
			// Get the type of the custom markup extension
			var markupTypeDef = member
				.Objects
				.FirstOrDefault(o => IsCustomMarkupExtensionType(o.Type));

			// Build a string of all its properties
			var properties = markupTypeDef
				.Members
				.Select(m =>
				{
					var resourceName = GetStaticResourceName(m);

					var value = resourceName != null
						? resourceName
						: BuildLiteralValue(m, owner: member);

					return "{0} = {1}".InvariantCultureFormat(m.Member.Name, value);
				})
				.JoinBy(", ");

			// Get the full globalized namespaces for the custom markup extension and also for IMarkupExtensionOverrides
			var markupType = GetType(markupTypeDef.Type);
			var markupTypeFullName = GetGlobalizedTypeName(markupType.GetFullName());
			var xamlMarkupFullName = GetGlobalizedTypeName(XamlConstants.Types.IMarkupExtensionOverrides);

			// Get the attribute from the custom markup extension class then get the return type specifed with MarkupExtensionReturnTypeAttribute
			var attributeData = markupType.FindAttribute(XamlConstants.Types.MarkupExtensionReturnTypeAttribute);
			var returnType = attributeData?.NamedArguments.FirstOrDefault(kvp => kvp.Key == "ReturnType").Value.Value;
			var cast = string.Empty;
			var provideValue = $"(({xamlMarkupFullName})(new {markupTypeFullName} {{ {properties} }})).ProvideValue()";

			if (returnType != null)
			{
				// A MarkupExtensionReturnType was specified, simply get type to cast against ProvideValue()
				cast = $"({returnType})";
			}
			else if (FindPropertyType(member.Member) is INamedTypeSymbol propertyType)
			{
				// MarkupExtensionReturnType wasn't specified...

				if (IsImplementingInterface(propertyType, _iConvertibleSymbol))
				{
					// ... and the target property implements IConvertible, therefore
					// cast ProvideValue() using Convert.ChangeType
					var targetTypeDisplay = propertyType.ToDisplayString();
					var targetType = $"typeof({targetTypeDisplay})";

					// It's important to cast to string before performing the conversion
					provideValue = $"Convert.ChangeType(({ provideValue}).ToString(), {targetType})";
					cast = $"({targetTypeDisplay})";
				}
				else
				{
					// ... and the target property is not an IConvertible, we cast
					// ProvideValue() using the type of the target property
					cast = GetCastString(propertyType, null);
				}
			}
			else
			{
				this.Log().Error($"Unable to determine the return type needed for the markup extension (a MarkupExtensionReturnType attribute is not available, and {member.Member} cannot be found).");
				return string.Empty;
			}

			return "{0}{1}".InvariantCultureFormat(cast, provideValue);
		}

		private bool IsMemberInsideDataTemplate(XamlObjectDefinition xamlObject)
			=> IsMemberInside(xamlObject, "DataTemplate");

		private bool IsMemberInsideFrameworkTemplate(XamlObjectDefinition xamlObject)
			=> IsMemberInside(xamlObject, "DataTemplate")
			|| IsMemberInside(xamlObject, "ControlTemplate")
			|| IsMemberInside(xamlObject, "ItemsPanelTemplate");

		private bool IsMemberInsideResourceDictionary(XamlObjectDefinition xamlObject)
			=> IsMemberInside(xamlObject, "ResourceDictionary", maxDepth: 1);

		private static bool IsMemberInside(XamlObjectDefinition xamlObject, string typeName, int? maxDepth = null)
		{
			if (xamlObject == null)
			{
				return false;
			}

			int depth = 0;
			do
			{
				if (xamlObject.Type?.Name == typeName)
				{
					return true;
				}

				xamlObject = xamlObject.Owner;
			}
			while (xamlObject != null && (maxDepth == null || ++depth <= maxDepth));

			return false;
		}

		/// <summary>
		/// Gets the key referred to by a StaticResourceExtension or ThemeResourceExtension.
		/// </summary>
		/// <param name="member">The StaticResourceExtension or ThemeResourceExtension member</param>
		/// <returns>(Key referred to, true if ThemeResource)</returns>
		private (string key, bool isThemeResourceExtension) GetStaticResourceKey(XamlMemberDefinition member)
		{
			var staticResourceNode = member.Objects.FirstOrDefault(o => o.Type.Name == "StaticResource");
			var themeResourceNode = member.Objects.FirstOrDefault(o => o.Type.Name == "ThemeResource");

			var staticResourcePath = staticResourceNode?.Members.First().Value.ToString();
			var themeResourcePath = themeResourceNode?.Members.First().Value.ToString();

			return (staticResourcePath ?? themeResourcePath, themeResourceNode != null);
		}

		/// <summary>
		/// Returns code for simple initialization-time retrieval for StaticResource/ThemeResource markup.
		/// </summary>
		private string GetSimpleStaticResourceRetrieval(XamlMemberDefinition member, INamedTypeSymbol targetPropertyType = null)
		{
			//Both ThemeResource and StaticResource use the same lookup mechanism
			var resourcePath = GetStaticResourceKey(member).key;

			if (resourcePath == null)
			{
				return null;
			}
			else
			{
				targetPropertyType = targetPropertyType ?? FindPropertyType(member.Member);

				return GetSimpleStaticResourceRetrieval(targetPropertyType, resourcePath);
			}
		}

		/// <summary>
		/// Returns code for simple initialization-time retrieval for StaticResource/ThemeResource markup.
		/// </summary>
		private string GetSimpleStaticResourceRetrieval(INamedTypeSymbol targetPropertyType, string keyStr)
		{
			var directProperty = GetResourceDictionaryPropertyName(keyStr);
			if (directProperty != null)
			{
				return directProperty;
			}

			targetPropertyType = targetPropertyType ?? _objectSymbol;
			return "global::Uno.UI.ResourceResolver.ResolveResourceStatic<{0}>(\"{1}\")"
				.InvariantCultureFormat(targetPropertyType.ToDisplayString(), keyStr);
		}

		/// <summary>
		/// Get the name of global static property associated with the given resource key, if one exists, otherwise null.
		/// </summary>
		private string GetResourceDictionaryPropertyName(string keyStr)
		{
			if (_topLevelDictionaryProperties.TryGetValue((_themeDictionaryCurrentlyBuilding, keyStr), out var propertyName))
			{
				return "global::{0}.GlobalStaticResources.{1} /*{2}*/".InvariantCultureFormat(_defaultNamespace, propertyName, keyStr);
			}

			return null;
		}

		private string BuildLiteralValue(INamedTypeSymbol propertyType, string memberValue, XamlMemberDefinition owner = null, string memberName = "", string objectUid = "")
		{
			if (IsLocalizedString(propertyType, objectUid))
			{
				var resourceValue = BuildLocalizedResourceValue(owner, memberName, objectUid);

				if (resourceValue != null)
				{
					return resourceValue;
				}
			}

			switch (propertyType.ToDisplayString())
			{
				case "int":
				case "long":
				case "short":
				case "byte":
					return memberValue;

				case "float":
				case "double":
					return GetFloatingPointLiteral(memberValue, propertyType, owner);

				case "string":
					return "\"" + DoubleEscape(memberValue) + "\"";

				case "bool":
					return Boolean.Parse(memberValue).ToString().ToLowerInvariant();

				case XamlConstants.Types.Brush:
				case XamlConstants.Types.SolidColorBrush:
					return BuildBrush(memberValue);

				case XamlConstants.Types.Thickness:
					return BuildThickness(memberValue);

				case XamlConstants.Types.CornerRadius:
					return $"new {XamlConstants.Types.CornerRadius}({memberValue})";

				case XamlConstants.Types.FontFamily:
					return $@"new {propertyType.ToDisplayString()}(""{memberValue}"")";

				case XamlConstants.Types.FontWeight:
					return BuildFontWeight(memberValue);

				case XamlConstants.Types.GridLength:
					return BuildGridLength(memberValue);

				case "UIKit.UIColor":
					return BuildColor(memberValue);

				case "Windows.UI.Color":
					return BuildColor(memberValue);

				case "Android.Graphics.Color":
					return BuildColor(memberValue);

				case "System.Uri":
					if (memberValue.StartsWith("/"))
					{
						return "new System.Uri(\"ms-appx://" + memberValue + "\")";
					}
					else
					{
						return "new System.Uri(\"" + memberValue + "\", global::System.UriKind.RelativeOrAbsolute)";
					}

				case "System.Type":
					return $"typeof({GetGlobalizedTypeName(GetType(memberValue).ToDisplayString())})";

				case XamlConstants.Types.Geometry:
					if (_isWasm)
					{
						return $"@\"{memberValue}\"";
					}
					var generated = Parsers.ParseGeometry(memberValue, CultureInfo.InvariantCulture);
					return generated;

				case XamlConstants.Types.KeyTime:
					return ParseTimeSpan(memberValue);

				case XamlConstants.Types.Duration:
					return $"new Duration({ ParseTimeSpan(memberValue) })";

				case "System.TimeSpan":
					return ParseTimeSpan(memberValue);

				case "System.Drawing.Point":
					return "new System.Drawing.Point(" + memberValue + ")";

				case "Windows.UI.Xaml.Media.CacheMode":
					return ParseCacheMode(memberValue);

				case "System.Drawing.PointF":
					return "new System.Drawing.PointF(" + AppendFloatSuffix(memberValue) + ")";

				case "System.Drawing.Size":
					return "new System.Drawing.Size(" + memberValue + ")";

				case "Windows.Foundation.Size":
					return "new Windows.Foundation.Size(" + memberValue + ")";

				case "Windows.UI.Xaml.Media.Matrix":
					return "new Windows.UI.Xaml.Media.Matrix(" + memberValue + ")";

				case "Windows.Foundation.Point":
					return "new Windows.Foundation.Point(" + memberValue + ")";

				case "Windows.UI.Xaml.Input.InputScope":
					return "new global::Windows.UI.Xaml.Input.InputScope { Names = { new global::Windows.UI.Xaml.Input.InputScopeName { NameValue = global::Windows.UI.Xaml.Input.InputScopeNameValue." + memberValue + "} } }";

				case "UIKit.UIImage":
					if (memberValue.StartsWith(XamlConstants.BundleResourcePrefix, StringComparison.InvariantCultureIgnoreCase))
					{
						return "UIKit.UIImage.FromBundle(\"" + memberValue.Substring(XamlConstants.BundleResourcePrefix.Length, memberValue.Length - XamlConstants.BundleResourcePrefix.Length) + "\")";
					}
					return memberValue;

				case "Windows.UI.Xaml.Controls.IconElement":
					return "new Windows.UI.Xaml.Controls.SymbolIcon { Symbol = Windows.UI.Xaml.Controls.Symbol." + memberValue + "}";

				case "Windows.Media.Playback.IMediaPlaybackSource":
					return "Windows.Media.Core.MediaSource.CreateFromUri(new Uri(\"" + memberValue + "\"))";
			}

			var isEnum = propertyType
				.TypeKind == TypeKind.Enum;

			if (isEnum)
			{
				var validFlags = propertyType.GetFields().Select(field => field.Name);
				var actualFlags = memberValue.Split(',').Select(part => part.Trim());

				var invalidFlags = actualFlags.Except(validFlags, StringComparer.OrdinalIgnoreCase);
				if (invalidFlags.Any())
				{
					throw new Exception($"The following values are not valid members of the '{propertyType.Name}' enumeration: {string.Join(", ", invalidFlags)}");
				}

				var finalFlags = validFlags.Intersect(actualFlags, StringComparer.OrdinalIgnoreCase);
				return string.Join("|", finalFlags.Select(flag => $"{propertyType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{flag}"));
			}

			var hasImplictToString = propertyType
				.GetMethods()
				.Any(m =>
					m.Name == "op_Implicit"
					&& m.Parameters.FirstOrDefault().SelectOrDefault(p => p.Type.ToDisplayString() == "string")
				);

			if (hasImplictToString

				// Can be an object (e.g. in case of Binding.ConverterParameter).
				|| propertyType.ToDisplayString() == "object"
			)
			{
				return "@\"" + memberValue.ToString() + "\"";
			}

			if (memberValue == null && propertyType.IsReferenceType)
			{
				return "null";
			}

			throw new Exception("Unable to convert {0} for {1} with type {2}".InvariantCultureFormat(memberValue, memberName, propertyType));
		}

		private string BuildLocalizedResourceValue(XamlMemberDefinition owner, string memberName, string objectUid)
		{
			// see: https://docs.microsoft.com/en-us/windows/uwp/app-resources/localize-strings-ui-manifest
			// Valid formats:
			// - MyUid
			// - MyPrefix/MyUid
			// - /ResourceFileName/MyUid
			// - /ResourceFileName/MyPrefix/MyUid
			// - /ResourceFilename/MyPrefix1/MyPrefix2/MyUid
			// - /ResourceFilename/MyPrefix1/MyPrefix2/MyPrefix3/MyUid

			(string resourceFileName, string uidName) parseXUid()
			{
				if (objectUid.StartsWith("/"))
				{
					var separator = objectUid.IndexOf('/', 1);

					return (
						objectUid.Substring(1, separator - 1),
						objectUid.Substring(separator + 1)
					);
				}
				else
				{
					return (null, objectUid);
				}
			}

			var (resourceFileName, uidName) = parseXUid();

			//windows 10 localization concat the xUid Value with the member value (Text, Content, Header etc...)
			var fullKey = uidName + "/" + memberName;

			if (owner != null && IsAttachedProperty(owner))
			{
				var declaringType = GetType(owner.Member.DeclaringType);
				var nsRaw = declaringType.ContainingNamespace.GetFullName();
				var ns = nsRaw.Replace(".", "/");
				var type = declaringType.Name;
				fullKey = $"{uidName}/[using:{ns}]{type}/{memberName}";
			}

			if (_resourceKeys.Any(k => k == fullKey))
			{
				var resourceNameString = resourceFileName == null ? "" : $"\"{resourceFileName}\"";

				return $"global::Windows.ApplicationModel.Resources.ResourceLoader.GetForCurrentView({resourceNameString}).GetString(\"{fullKey}\")";
			}

			return null;
		}

		private string ParseCacheMode(string memberValue)
		{
			if (memberValue.Equals("BitmapCache", StringComparison.OrdinalIgnoreCase))
			{
				return "new global::Windows.UI.Xaml.Media.BitmapCache()";
			}

			throw new Exception($"The [{memberValue}] cache mode is not supported");
		}

		private static string DoubleEscape(string thisString)
		{
			//http://stackoverflow.com/questions/366124/inserting-a-tab-character-into-text-using-c-sharp
			return thisString
				.Replace("\\", "\\\\")
				.Replace("\"", "\\\"")
				.Replace("\n", "\\n")
				.Replace("\r", "\\r")
				.Replace("\t", "\\t");
		}

		private static string ParseTimeSpan(string memberValue)
		{
			var value = TimeSpan.Parse(memberValue);

			return $"global::System.TimeSpan.FromTicks({value.Ticks} /* {memberValue} */)";
		}

		private static string BuildGridLength(string memberValue)
		{
			var gridLength = Windows.UI.Xaml.GridLength.ParseGridLength(memberValue).FirstOrDefault();

			return $"new {XamlConstants.Types.GridLength}({gridLength.Value.ToStringInvariant()}f, {XamlConstants.Types.GridUnitType}.{gridLength.GridUnitType})";
		}

		private string BuildLiteralValue(XamlMemberDefinition member, INamedTypeSymbol propertyType = null, XamlMemberDefinition owner = null, string objectUid = "")
		{
			if (member.Objects.None())
			{
				var memberValue = member.Value?.ToString();

				propertyType = propertyType ?? FindPropertyType(member.Member);

				if (propertyType != null)
				{
					return BuildLiteralValue(propertyType, memberValue, owner, member.Member.Name, objectUid);
				}
				else
				{
					throw new Exception($"The property {member.Owner?.Type?.Name}.{member.Member?.Name} is unknown".InvariantCultureFormat(member.Member?.Name));
				}
			}
			else
			{
				var expression = member.Objects.First();

				if (expression.Type.Name == "StaticResource" || expression.Type.Name == "ThemeResource")
				{
					return GetSimpleStaticResourceRetrieval(null, expression.Members.First().Value?.ToString());
				}
				else if (expression.Type.Name == "NullExtension")
				{
					return "null";
				}
				else
				{
					throw new NotSupportedException("MarkupExtension {0} is not supported.".InvariantCultureFormat(expression.Type.Name));
				}
			}
		}

		private string BuildFontWeight(string memberValue)
		{
			var fontWeights = (INamedTypeSymbol)_metadataHelper.GetTypeByFullName(XamlConstants.Types.FontWeights);

			if (fontWeights.GetFields().Any(m => m.Name.Equals(memberValue, StringComparison.OrdinalIgnoreCase)))
			{
				return "FontWeights." + memberValue;
			}
			else
			{
				return "FontWeights.Normal /* Warning {0} is not supported on this platform */".InvariantCultureFormat(memberValue);
			}
		}

		private string BuildBrush(string memberValue)
		{
			var colorHelper = (INamedTypeSymbol)_metadataHelper.GetTypeByFullName(XamlConstants.Types.SolidColorBrushHelper);

			// This ensures that a memberValue "DarkGoldenRod" gets converted to colorName "DarkGoldenrod" (notice the lowercase 'r')
			var colorName = colorHelper.GetProperties().FirstOrDefault(m => m.Name.Equals(memberValue, StringComparison.OrdinalIgnoreCase))?.Name;
			if (colorName != null)
			{
				return "SolidColorBrushHelper." + colorName;
			}
			else
			{
				memberValue = string.Join(", ", ColorCodeParser.ParseColorCode(memberValue));

				return "SolidColorBrushHelper.FromARGB({0})".InvariantCultureFormat(memberValue);
			}
		}

		private static string BuildThickness(string memberValue)
		{
			// This is until we find an appropriate way to convert strings to Thickness.
			if (!memberValue.Contains(","))
			{
				memberValue = memberValue.Replace(" ", ",");
			}

			if (memberValue.Contains("."))
			{
				// Append 'f' to every decimal value in the thickness
				memberValue = AppendFloatSuffix(memberValue);
			}

			return "new global::Windows.UI.Xaml.Thickness(" + memberValue + ")";
		}

		private static string AppendFloatSuffix(string memberValue)
		{
			return memberValue.Split(',')
					.Select(s => s.Contains(".") ? s + "f" : s)
					.Aggregate((s1, s2) => "{0},{1}".InvariantCultureFormat(s1, s2));
		}

		private string BuildColor(string memberValue)
		{
			var colorHelper = (INamedTypeSymbol)_metadataHelper.GetTypeByFullName(XamlConstants.Types.Colors);

			if (colorHelper.GetFields().Any(m => m.Name.Equals(memberValue, StringComparison.OrdinalIgnoreCase)))
			{
				return $"{XamlConstants.Types.Colors}.{memberValue}";
			}
			else
			{
				memberValue = string.Join(", ", ColorCodeParser.ParseColorCode(memberValue));

				return "Windows.UI.ColorHelper.FromARGB({0})".InvariantCultureFormat(memberValue);
			}
		}

		private string BuildBindingOption(XamlMemberDefinition m, INamedTypeSymbol propertyType, bool prependCastToType)
		{
			// The default member is Path
			var memberName = m.Member.Name == "_PositionalParameters" ? "Path" : m.Member.Name;

			if (m.Objects.Any())
			{
				var bindingType = m.Objects.First();

				if (
					bindingType.Type.Name == "StaticResource"
					|| bindingType.Type.Name == "ThemeResource"
					)
				{
					var resourceName = bindingType.Members.First().Value.ToString();
					return GetSimpleStaticResourceRetrieval(propertyType, resourceName);
				}

				if (bindingType.Type.Name == "RelativeSource")
				{
					var resourceName = bindingType.Members.First().Value.ToString();

					return $"new RelativeSource(RelativeSourceMode.{resourceName})";
				}

				// If type specified in the binding was not found, log and return an error message
				if (!string.IsNullOrEmpty(bindingType?.Type?.Name ?? string.Empty))
				{
					var message = $"#Error // {bindingType.Type.Name} could not be found.";
					this.Log().Error(message);

					return message;
				}

				return "#Error";
			}
			else
			{

				var value = BuildLiteralValue(m, GetPropertyType("Binding", memberName));

				if (memberName == "Path")
				{
					value = RewriteAttachedPropertyPath(value);
				}
				else if (memberName == "ElementName")
				{
					// Skip the literal value, use the elementNameSubject instead
					string elementName = m.Value.ToString();
					value = "_" + elementName + "Subject";
					// Track referenced ElementNames
					CurrentScope.ReferencedElementNames.Add(elementName);
				}
				else if (memberName == "FallbackValue"
					|| (memberName == "TargetNullValue"
						&& m.Owner.Members.None(otherMember => otherMember.Member.Name == "Converter")))
				{
					// FallbackValue can match the type of the property being bound.
					// TargetNullValue possibly doesn't match the type of the property being bound,
					// if there is a converter
					value = BuildLiteralValue(m, propertyType);
				}


				return value;
			}
		}

		private string RewriteAttachedPropertyPath(string value)
		{
			if (value.Contains("("))
			{
				foreach (var ns in _fileDefinition.Namespaces)
				{
					if (ns != null)
					{
						var clrNamespace = ns.Namespace.TrimStart("using:");

						value = value.Replace("(" + ns.Prefix + ":", "(" + clrNamespace + ":");
					}
				}

				var match = Regex.Match(value, @"(\(.*?\))");

				if (match.Success)
				{
					do
					{
						if (!match.Value.Contains(":"))
						{
							// if there is no ":" this means that the type is using the default
							// namespace, so try to resolve the best way we can.

							var parts = match.Value.Trim(new[] { '(', ')' }).Split('.');

							if (parts.Length == 2)
							{
								var targetType = SourceFindType(parts[0]);

								if (targetType != null)
								{
									var newPath = targetType.ContainingNamespace.ToDisplayString() + ":" + targetType.Name + "." + parts[1];

									value = value.Replace(match.Value, '(' + newPath + ')');
								}
							}
						}
					}
					while ((match = match.NextMatch()).Success);
				}
			}

			return value;
		}

		private void BuildLiteralProperties(IIndentedStringBuilder writer, XamlObjectDefinition objectDefinition, string closureName = null)
		{
			var closingPunctuation = string.IsNullOrWhiteSpace(closureName) ? "," : ";";

			BuildStyleProperty(writer, objectDefinition, closingPunctuation);

			var extendedProperties = GetExtendedProperties(objectDefinition);

			var objectUid = GetObjectUid(objectDefinition);

			if (extendedProperties.Any())
			{
				foreach (var member in extendedProperties)
				{
					var fullValueSetter = string.IsNullOrWhiteSpace(closureName) ? member.Member.Name : "{0}.{1}".InvariantCultureFormat(closureName, member.Member.Name);

					// Exclude attached properties, must be set in the extended apply section.
					// If there is no type attached, this can be a binding.
					if (IsType(objectDefinition.Type, member.Member.DeclaringType)
						&& !IsAttachedProperty(member)
						&& FindEventType(member.Member) == null
						&& member.Member.Name != "_UnknownContent" // We are defining the elements of a collection explicitly declared in XAML
					)
					{
						if (member.Objects.None())
						{
							if (IsInitializableCollection(member.Member))
							{
								writer.AppendLineInvariant("// Empty collection");
							}
							else
							{
								if (FindPropertyType(member.Member) != null)
								{
									writer.AppendLineInvariant("{0} = {1}{2}", fullValueSetter, BuildLiteralValue(member, objectUid: objectUid), closingPunctuation);
								}
								else
								{
									if (IsRelevantNamespace(member?.Member?.PreferredXamlNamespace)
										&& IsRelevantProperty(member?.Member))
									{
										GenerateError(
											writer,
											"Property {0} does not exist on {1}, name is {2}, preferrednamespace {3}",
											fullValueSetter,
											member?.Member?.DeclaringType,
											member?.Member?.Name,
											member?.Member?.PreferredXamlNamespace);
									}
									else
									{
										GenerateSilentWarning(
											writer,
											"Property {0} does not exist on {1}, the namespace is {2}. This error was considered irrelevant by the XamlFileGenerator",
											fullValueSetter,
											member?.Member?.DeclaringType,
											member?.Member?.PreferredXamlNamespace);
									}
								}
							}
						}
						else
						{
							var nonBindingObjects = member
								.Objects
								.Where(m =>
									m.Type.Name != "Binding"
									&& m.Type.Name != "Bind"
									&& m.Type.Name != "StaticResource"
									&& m.Type.Name != "ThemeResource"
									&& m.Type.Name != "TemplateBinding"
									&& !IsCustomMarkupExtensionType(m.Type)
								);

							if (nonBindingObjects.Any())
							{
								var isInitializableCollection = IsInitializableCollection(member.Member);
								var isInlineCollection = IsInlineCollection(member.Member, nonBindingObjects);

								if (isInlineCollection && closureName != null)
								{
									foreach (var child in nonBindingObjects)
									{
										writer.AppendLineInvariant($"{fullValueSetter}.Add(");
										using (writer.Indent())
										{
											BuildChild(writer, member, child);
										}
										writer.AppendLineInvariant(");");
									}
								}
								else if (isInitializableCollection || isInlineCollection)
								{
									using (writer.BlockInvariant($"{fullValueSetter} = "))
									{
										foreach (var child in nonBindingObjects)
										{
											BuildChild(writer, member, child);
											writer.AppendLineInvariant(",");
										}
									}
								}
								else
								{
									writer.AppendFormatInvariant($"{fullValueSetter} = ");
									BuildChild(writer, member, nonBindingObjects.First());
								}

								writer.AppendLineInvariant(closingPunctuation);
							}
						}
					}
					else if (member.Member.DeclaringType == null && member.Member.Name == "Name")
					{
						// This is a special case, where the declaring type is from the x: namespace,
						// but is considered of an unknown type. This can happen when providing the
						// name of a control using x:Name instead of Name.
						var hasNameProperty = HasProperty(objectDefinition.Type, "Name");
						if (hasNameProperty)
						{
							writer.AppendLineInvariant("{0} = \"{1}\"{2}", fullValueSetter, member.Value, closingPunctuation);
						}
					}
				}
			}
		}

		/// <summary>
		/// Determines if the member is inline initializable and the first item is not a new collection instance
		/// </summary>
		private bool IsInlineCollection(XamlMember member, IEnumerable<XamlObjectDefinition> elements)
		{
			var declaringType = member.DeclaringType;
			var propertyName = member.Name;
			var property = GetPropertyByName(declaringType, propertyName);

			if (property?.Type is INamedTypeSymbol collectionType)
			{
				if (IsCollectionOrListType(collectionType) && elements.FirstOrDefault() is XamlObjectDefinition first)
				{
					var firstElementType = GetType(first.Type);

					return collectionType != firstElementType;
				}
			}

			return false;
		}

		private void BuildStyleProperty(IIndentedStringBuilder writer, XamlObjectDefinition objectDefinition, string closingPunctuation)
		{
			var styleMember = FindMember(objectDefinition, "Style");

			if (styleMember != null)
			{
				if (styleMember.Objects.Any(o => o.Type.Name == "StaticResource" || o.Type.Name == "ThemeResource"))
				{
					// Skip here - Styles assigned as StaticResources don't receive special treatment.
				}
				else if (styleMember.Objects.FirstOrDefault(o => o.Type.Name == "Style") is XamlObjectDefinition literalStyle)
				{
					writer.AppendFormatInvariant($"Style = ");
					BuildInlineStyle(writer, literalStyle);
					writer.AppendLineInvariant(0, closingPunctuation);
				}
			}
		}

		private bool IsLocalizedString(INamedTypeSymbol propertyType, string objectUid)
		{
			return objectUid.HasValue() && IsLocalizablePropertyType(propertyType);
		}

		private bool IsLocalizablePropertyType(INamedTypeSymbol propertyType)
		{
			return Equals(propertyType, _stringSymbol)
				|| Equals(propertyType, _objectSymbol);
		}

		private string GetObjectUid(XamlObjectDefinition objectDefinition)
		{
			string objectUid = null;
			var localizedObject = FindMember(objectDefinition, "Uid");
			if (localizedObject != null)
			{
				objectUid = localizedObject.Value.ToString();
			}

			return objectUid;
		}

		/// <summary>
		/// Gets a basic class that implements a know collection or list interface
		/// </summary>
		private bool GetKnownNewableListOrCollectionInterface(INamedTypeSymbol type, out string newableTypeName)
		{
			switch (type?.Name)
			{
				case "ICollection":
				case "IList":
					newableTypeName = type.IsGenericType ?
						"List<{0}>".InvariantCultureFormat(GetFullGenericTypeName(type.TypeArguments.Single() as INamedTypeSymbol)) :
						"List<System.Object>";
					return true;
			};

			newableTypeName = null;
			return false;
		}

		private IEnumerable<XamlMemberDefinition> GetExtendedProperties(XamlObjectDefinition objectDefinition)
		{
			var objectUid = GetObjectUid(objectDefinition);
			var hasUnkownContentOnly = objectDefinition.Members.All(m => m.Member.Name == "_UnknownContent");

			return objectDefinition
				.Members
				.Where(m =>
					(
						m.Member.Name != "_UnknownContent"

						&& m.Member.Name != "Resources"
						&& m.Member.Name != "Key"
					)
					||
					(
						m.Member.Name == "Content" && HasMarkupExtension(m)
					)
					||
					(
						m.Member.Name == "Content" && IsLocalizedString(FindPropertyType(m.Member), objectUid)
					)
					||
					(
						m.Member.Name == "Style" && HasBindingMarkupExtension(m)
					)
					|| IsAttachedProperty(m)
					||
					(
						// If the object is a collection and it has both _UnknownContent (i.e. an item) and other properties defined,
						// we cannot use property ADN collection initializer syntax at same time, so we will add items using an ApplyBlock.
						m.Member.Name == "_UnknownContent" && !hasUnkownContentOnly && FindType(m.Owner.Type) is INamedTypeSymbol type && IsCollectionOrListType(type)
					)
				);
		}

		private void BuildChild(IIndentedStringBuilder writer, XamlMemberDefinition owner, XamlObjectDefinition xamlObjectDefinition)
		{
			var typeName = xamlObjectDefinition.Type.Name;
			var fullTypeName = xamlObjectDefinition.Type.Name;

			var knownType = FindType(xamlObjectDefinition.Type);

			if (knownType == null && xamlObjectDefinition.Type.PreferredXamlNamespace.StartsWith("using:"))
			{
				fullTypeName = xamlObjectDefinition.Type.PreferredXamlNamespace.TrimStart("using:") + "." + xamlObjectDefinition.Type.Name;
			}

			if (knownType != null)
			{
				// Override the using with the type that was found in the list of loaded assemblies
				fullTypeName = knownType.ToDisplayString();
			}

			var isItemsPanelTemplate = typeName == "ItemsPanelTemplate";

			if (
				typeName == "DataTemplate"
				|| isItemsPanelTemplate
				|| typeName == "ControlTemplate"

				// This case is specific the custom ListView for iOS. Should be removed
				// when the list rebuilt to be compatible.
				|| typeName == "ListViewBaseLayoutTemplate"
			)
			{
				writer.AppendFormatInvariant("new {0}(() => ", GetGlobalizedTypeName(fullTypeName));

				var contentOwner = xamlObjectDefinition.Members.FirstOrDefault(m => m.Member.Name == "_UnknownContent");

				if (contentOwner != null)
				{
					// This case is to support the layout switching for the ListViewBaseLayout, which is not
					// a FrameworkTemplate. This will need to be removed when this custom list view is removed.
					var returnType = typeName == "ListViewBaseLayoutTemplate" ? "Uno.UI.Controls.Legacy.ListViewBaseLayout" : "_View";

					BuildChildThroughSubclass(writer, contentOwner, returnType);

					writer.AppendFormatInvariant(")");
				}
				else
				{
					writer.AppendFormatInvariant("/* This template does not have a content for this platform */");
				}
			}
			else if (typeName == "NullExtension")
			{
				writer.AppendFormatInvariant("null");
			}
			else if (
				typeName == "StaticResource"
				|| typeName == "ThemeResource"
				|| typeName == "Binding"
				|| typeName == "Bind"
				|| typeName == "TemplateBinding"
				)
			{
				BuildComplexPropertyValue(writer, owner, "c.");
			}
			else if (
				_skipUserControlsInVisualTree
				&& IsDirectUserControlSubType(xamlObjectDefinition)
				&& HasNoUserControlProperties(xamlObjectDefinition))
			{
				writer.AppendLineInvariant("new {0}(skipsInitializeComponents: true).GetContent()", GetGlobalizedTypeName(fullTypeName));

				using (var innerWriter = CreateApplyBlock(writer, null, out var closureName))
				{
					RegisterAndBuildResources(writer, xamlObjectDefinition, isInInitializer: true);
					BuildLiteralProperties(innerWriter, xamlObjectDefinition, closureName);
					BuildProperties(innerWriter, xamlObjectDefinition, closureName: closureName);
				}

				BuildExtendedProperties(writer, xamlObjectDefinition, useGenericApply: true);
			}
			else if (HasInitializer(xamlObjectDefinition))
			{
				BuildInitializer(writer, xamlObjectDefinition, owner);
				BuildLiteralProperties(writer, xamlObjectDefinition);
			}
			else if (fullTypeName == XamlConstants.Types.Style)
			{
				BuildInlineStyle(writer, xamlObjectDefinition);
			}
			else if (fullTypeName == XamlConstants.Types.Setter)
			{
				var propertyNode = FindMember(xamlObjectDefinition, "Property");
				var targetNode = FindMember(xamlObjectDefinition, "Target");
				var valueNode = FindMember(xamlObjectDefinition, "Value");

				var property = propertyNode?.Value.SelectOrDefault(p => p.ToString());
				var target = targetNode?.Value.SelectOrDefault(p => p.ToString());

				if (property != null)
				{
					var value = BuildLiteralValue(valueNode);

					// This builds property setters for the owner of the setter.
					writer.AppendLineInvariant($"new Windows.UI.Xaml.Setter(new Windows.UI.Xaml.TargetPropertyPath(this, \"{property}\"), {value})");
				}
				else if (target != null)
				{
					// This builds property setters for specified member setter.
					var separatorIndex = target.IndexOf(".");
					var elementName = target.Substring(0, separatorIndex);
					var propertyName = target.Substring(separatorIndex + 1);

					var ownerControl = GetControlOwner(owner.Owner);

					string value = BuildLiteralValue(valueNode);

					if (ownerControl != null)
					{
						var targetElement = FindSubElementByName(ownerControl, elementName);

						if (targetElement != null)
						{
							var targetElementType = GetType(targetElement.Type);
							var propertyType = FindPropertyType(targetElementType.ToString(), propertyName);

							if (propertyType != null)
							{
								if (HasMarkupExtension(valueNode))
								{
									value = GetSimpleStaticResourceRetrieval(valueNode, propertyType);
								}
								else
								{
									value = BuildLiteralValue(valueNode, propertyType);
								}
							}
						}
					}

					// Attached properties need to be expanded using the namespace, otherwise the resolution will be
					// performed at runtime at a higher cost.
					propertyName = RewriteAttachedPropertyPath(propertyName);

					writer.AppendLineInvariant($"new global::Windows.UI.Xaml.Setter(new global::Windows.UI.Xaml.TargetPropertyPath(this._{elementName}Subject, \"{propertyName}\"), {value.Replace("{", "{{").Replace("}", "}}")})");
				}
			}
			else
			{
				var hasCustomInitalizer = HasCustomInitializer(xamlObjectDefinition);

				if (hasCustomInitalizer)
				{
					var propertyType = FindType(xamlObjectDefinition.Type);
					writer.AppendLine(BuildLiteralValue(FindImplicitContentMember(xamlObjectDefinition), propertyType, owner));
				}
				else
				{
					using (TryGenerateDeferedLoadStrategy(writer, knownType, xamlObjectDefinition))
					{
						using (writer.BlockInvariant("new {0}{1}", GetGlobalizedTypeName(fullTypeName), GenerateConstructorParameters(xamlObjectDefinition.Type)))
						{
							TrySetParsing(writer, xamlObjectDefinition);
							RegisterAndBuildResources(writer, xamlObjectDefinition, isInInitializer: true);
							BuildLiteralProperties(writer, xamlObjectDefinition);
							BuildProperties(writer, xamlObjectDefinition);
							BuildLocalizedProperties(writer, xamlObjectDefinition);
						}

						BuildExtendedProperties(writer, xamlObjectDefinition);
					}
				}
			}
		}

		/// <summary>
		/// Set the 'IsParsing' flag. This should be the first property set when an element is parsed.
		/// </summary>
		private void TrySetParsing(IIndentedStringBuilder writer, XamlObjectDefinition objectDefinition)
		{
			if (IsFrameworkElement(objectDefinition.Type))
			{
				writer.AppendLineInvariant("IsParsing = true,");
			}
		}

		private bool HasChildrenWithPhase(XamlObjectDefinition xamlObjectDefinition)
		{
			if (xamlObjectDefinition.Owner?.Type.Name == "DataTemplate")
			{

				var q = from element in EnumerateSubElements(xamlObjectDefinition.Owner)
						let phase = FindMember(element, "Phase")?.Value
						where phase != null
						select phase;

				return q.Any();
			}

			return false;
		}

		private string GenerateRootPhases(XamlObjectDefinition xamlObjectDefinition, string ownerVariable)
		{
			if (xamlObjectDefinition.Owner?.Type.Name == "DataTemplate")
			{
				var q = from element in EnumerateSubElements(xamlObjectDefinition.Owner)
						let phase = FindMember(element, "Phase")?.Value
						where phase != null
						select int.Parse(phase.ToString());

				var phases = q.Distinct().ToArray();

				if (phases.Any())
				{
					var phasesValue = phases.OrderBy(i => i).Select(s => s.ToString()).JoinBy(",");
					return $"Uno.UI.FrameworkElementHelper.SetDataTemplateRenderPhases({ownerVariable}, new []{{{phasesValue}}});";
				}
			}

			return null;
		}

		private bool HasNoUserControlProperties(XamlObjectDefinition objectDefinition)
		{
			return
			objectDefinition
				.Members
				.Where(m =>
					(
						m.Member.Name != "DataContext"
						&& m.Member.Name != "_UnknownContent"
					)
				)
				.None();
		}

		private XamlObjectDefinition GetControlOwner(XamlObjectDefinition owner)
		{
			do
			{
				var type = GetType(owner.Type);

				if (type.Is(XamlConstants.Types.Control))
				{
					return owner;
				}

				owner = owner.Owner;
			}
			while (owner != null);

			return null;
		}

		/// <summary>
		/// Statically finds a element by name, given a xaml element root
		/// </summary>
		/// <param name="xamlObject">The root from which to start the search</param>
		/// <param name="elementName">The x:Name value to search for</param>
		/// <returns></returns>
		private XamlObjectDefinition FindSubElementByName(XamlObjectDefinition xamlObject, string elementName)
		{
			foreach (var element in EnumerateSubElements(xamlObject))
			{
				var nameMember = FindMember(element, "Name");

				if (nameMember?.Value?.ToString() == elementName)
				{
					return element;
				}
			}

			return null;
		}

		/// <summary>
		/// Statically finds a element by name, given a xaml element root
		/// </summary>
		/// <param name="xamlObject">The root from which to start the search</param>
		/// <param name="elementName">The x:Name value to search for</param>
		/// <returns></returns>
		private IEnumerable<XamlObjectDefinition> EnumerateSubElements(XamlObjectDefinition xamlObject)
		{
			foreach (var member in xamlObject.Members)
			{
				foreach (var element in EnumerateSubElements(member.Objects))
				{
					yield return element;
				}
			}

			var objects = xamlObject.Objects;

			foreach (var element in EnumerateSubElements(objects))
			{
				yield return element;
			}
		}

		private IEnumerable<XamlObjectDefinition> EnumerateSubElements(IEnumerable<XamlObjectDefinition> objects)
		{
			foreach (var child in objects.Safe())
			{
				yield return child;

				foreach (var innerElement in EnumerateSubElements(child))
				{
					yield return innerElement;
				}
			}

			yield break;
		}

		private IDisposable TryGenerateDeferedLoadStrategy(IIndentedStringBuilder writer, INamedTypeSymbol targetType, XamlObjectDefinition definition)
		{
			var strategy = FindMember(definition, "DeferLoadStrategy");
			var loadElement = FindMember(definition, "Load");

			if (strategy?.Value?.ToString().ToLowerInvariant() == "lazy"
				|| loadElement?.Value?.ToString().ToLowerInvariant() == "false")
			{
				var visibilityMember = FindMember(definition, "Visibility");
				var dataContextMember = FindMember(definition, "DataContext");
				var nameMember = FindMember(definition, "Name");

				if (!targetType.Is(_elementStubSymbol.BaseType))
				{
					writer.AppendLineInvariant($"/* Lazy DeferLoadStrategy was ignored because the target type is not based on {_elementStubSymbol.BaseType} */");
					return null;
				}

				if (visibilityMember != null)
				{
					var hasVisibilityMarkup = HasMarkupExtension(visibilityMember);
					var isLiteralVisible = !hasVisibilityMarkup && (visibilityMember.Value?.ToString() == "Visible");

					var hasDataContextMarkup = dataContextMember != null && HasMarkupExtension(dataContextMember);

					var disposable = writer.BlockInvariant($"new {XamlConstants.Types.ElementStub}()");

					writer.Append("ContentBuilder = () => ");

					return new DisposableAction(() =>
						{
							disposable.Dispose();

							string closureName;
							using (var innerWriter = CreateApplyBlock(writer, GetType(XamlConstants.Types.ElementStub), out closureName))
							{
								if (hasDataContextMarkup)
								{
									// We need to generate the datacontext binding, since the Visibility
									// may require it to bind properly.

									var def = new XamlMemberDefinition(
										new XamlMember("DataContext",
											new XamlType("", "ElementStub", new List<XamlType>(), new XamlSchemaContext()),
											false
										), 0, 0
									);

									def.Objects.AddRange(dataContextMember.Objects);

									BuildComplexPropertyValue(innerWriter, def, closureName + ".", closureName);
								}

								if (hasVisibilityMarkup)
								{
									var def = new XamlMemberDefinition(
										new XamlMember("Visibility",
											new XamlType("", "ElementStub", new List<XamlType>(), new XamlSchemaContext()),
											false
										), 0, 0
									);

									def.Objects.AddRange(visibilityMember.Objects);

									BuildComplexPropertyValue(innerWriter, def, closureName + ".", closureName);
								}
								else
								{
									innerWriter.AppendLineInvariant(
										"{0}.Visibility = {1};",
										closureName,
										BuildLiteralValue(visibilityMember)
									);

									if (nameMember != null)
									{
										innerWriter.AppendLineInvariant(
											$"{closureName}.Name = \"{nameMember.Value}\";"
										);

										// Set the element name to the stub, then when the stub will be replaced
										// the actual target control will override it.
										innerWriter.AppendLineInvariant(
											$"_{nameMember.Value}Subject.ElementInstance = {closureName};"
										);
									}
								}
							}
						}
					);
				}
			}

			return null;
		}

		private void BuildChildThroughSubclass(IIndentedStringBuilder writer, XamlMemberDefinition contentOwner, string returnType)
		{
			// To prevent conflicting names whenever we are working with dictionaries, subClass index is a Guid in those cases
			var subClassPrefix = _scopeStack.Aggregate("", (val, scope) => val + scope.Name);

			var namespacePrefix = _scopeStack.Count == 1 && _scopeStack.Last().Name.EndsWith("RD") ? "__Resources." : "";

			var subclassName = $"_{_fileUniqueId}_{subClassPrefix}SC{(_subclassIndex++).ToString(CultureInfo.InvariantCulture)}";

			RegisterChildSubclass(subclassName, contentOwner, returnType);

			writer.AppendLineInvariant($"new {namespacePrefix}{subclassName}().Build()");
		}

		private string GenerateConstructorParameters(XamlType xamlType)
		{
			if (IsType(xamlType, "Android.Views.View"))
			{
				// For android, all native control must take a context as their first parameters
				// To be able to use this control from the Xaml, we need to generate a constructor
				// call that takes the ContextHelper.Current as the first parameter.

				var type = FindType(xamlType);

				if (type != null)
				{
					var q = from m in type.Constructors
							where m.Parameters.Count() == 1
							where m.Parameters.First().Type.ToDisplayString() == "Android.Content.Context"
							select m;

					var hasContextConstructor = q.Any();

					if (hasContextConstructor)
					{
						return "(Uno.UI.ContextHelper.Current)";
					}
				}
			}

			return "";
		}

		private bool HasCustomInitializer(XamlObjectDefinition definition)
		{
			var propertyType = FindType(definition.Type);

			if (propertyType != null)
			{
				if (propertyType.TypeKind == TypeKind.Enum)
				{
					return true;
				}

				switch (propertyType.ToDisplayString())
				{
					case "int":
					case "float":
					case "long":
					case "short":
					case "byte":
					case "double":
					case "string":
					case "bool":
					case XamlConstants.Types.Thickness:
					case XamlConstants.Types.FontFamily:
					case XamlConstants.Types.FontWeight:
					case XamlConstants.Types.GridLength:
					case XamlConstants.Types.CornerRadius:
					case XamlConstants.Types.Brush:
					case "UIKit.UIColor":
					case "Windows.UI.Color":
					case "Color":
						return true;
				}
			}

			return false;
		}

		private void BuildInitializer(IIndentedStringBuilder writer, XamlObjectDefinition xamlObjectDefinition, XamlMemberDefinition owner = null)
		{
			var initializer = xamlObjectDefinition.Members.First(m => m.Member.Name == "_Initialization");

			if (IsPrimitive(xamlObjectDefinition))
			{
				if (xamlObjectDefinition.Type.Name == "Double" || xamlObjectDefinition.Type.Name == "Single")
				{
					writer.AppendFormatInvariant("{0}", GetFloatingPointLiteral(initializer.Value.ToString(), GetType(xamlObjectDefinition.Type), owner));
				}
				else if (xamlObjectDefinition.Type.Name == "Boolean")
				{
					writer.AppendFormatInvariant("{1}", xamlObjectDefinition.Type.Name, initializer.Value.ToString().ToLowerInvariant());
				}
				else
				{
					writer.AppendFormatInvariant("{1}", xamlObjectDefinition.Type.Name, initializer.Value);
				}
			}
			else if (IsString(xamlObjectDefinition))
			{
				writer.AppendFormatInvariant("\"{1}\"", xamlObjectDefinition.Type.Name, DoubleEscape(initializer.Value?.ToString()));
			}
			else
			{
				writer.AppendFormatInvariant("new {0}({1})", GetGlobalizedTypeName(xamlObjectDefinition.Type.Name), initializer.Value);
			}
		}

		private string GetFloatingPointLiteral(string memberValue, INamedTypeSymbol type, XamlMemberDefinition owner)
		{
			var name = ValidatePropertyType(type, owner);

			var isDouble = IsDouble(name);

			if (memberValue.EndsWith("nan", StringComparison.OrdinalIgnoreCase) || memberValue.Equals("auto", StringComparison.OrdinalIgnoreCase))
			{
				return "{0}.NaN".InvariantCultureFormat(isDouble ? "double" : "float");
			}
			else if (memberValue.ToLowerInvariant().EndsWith("positiveinfinity"))
			{
				return "{0}.PositiveInfinity".InvariantCultureFormat(isDouble ? "double" : "float");
			}
			else if (memberValue.ToLowerInvariant().EndsWith("negativeinfinity"))
			{
				return "{0}.NegativeInfinity".InvariantCultureFormat(isDouble ? "double" : "float");
			}
			else if (memberValue.ToLowerInvariant().EndsWith("px"))
			{
				this.Log().Warn($"The value [{memberValue}] is specified in pixel and is not yet supported. ({owner?.LineNumber}, {owner?.LinePosition} in {_fileDefinition.FilePath})");
				return "{0}{1}".InvariantCultureFormat(memberValue.TrimEnd("px"), isDouble ? "d" : "f");
			}

			return "{0}{1}".InvariantCultureFormat(memberValue, isDouble ? "d" : "f");
		}

		private string ValidatePropertyType(INamedTypeSymbol propertyType, XamlMemberDefinition owner)
		{
			if (IsDouble(propertyType.ToDisplayString()) &&
				owner != null && (
				owner.Member.Name == "Width" ||
				owner.Member.Name == "Height" ||
				owner.Member.Name == "MinWidth" ||
				owner.Member.Name == "MinHeight" ||
				owner.Member.Name == "MaxWidth" ||
				owner.Member.Name == "MaxHeight"
				))
			{
				return "float";
			}

			return propertyType.ToDisplayString();
		}

		private bool IsDirectUserControlSubType(XamlObjectDefinition objectDefinition)
		{
			return string.Equals(
				FindType(objectDefinition.Type)
					?.BaseType
					?.ToDisplayString(),
				XamlConstants.Types.UserControl);
		}

		private NameScope CurrentScope
		{
			get
			{
				return _scopeStack.Peek();
			}
		}

		private IDisposable Scope(string name)
		{
			_scopeStack.Push(new NameScope(name));

			return new DisposableAction(() => _scopeStack.Pop());
		}
	}
}
