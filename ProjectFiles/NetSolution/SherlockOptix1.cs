#region Using directives
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using FTOptix.CommunicationDriver;
using FTOptix.HMIProject;
using FTOptix.NetLogic;
using FTOptix.CoreBase;
using UAManagedCore;
using FTOptix.UI;
using FTOptix.MQTTBroker;
using FTOptix.MQTTClient;
using FTOptix.Modbus;
#endregion


public class SherlockOptix1 : BaseNetLogic
{
    private const string DiagnosticTag = "ProjectAnalyzer";
    private Dictionary<string, string> _projectCategoryByNodeId;

    // GetProjectDiagnostics is a DesignTime ExportMethod: Optix creates a brand-new
    // ProjectDiagnostic instance (and reloads the assembly) for EVERY invocation, so an
    // instance (or static) field can never be seen by a later call and cannot guard against
    // overlapping runs. A marker file next to the report output is the only state that
    // actually survives across calls; its mtime also lets a crashed/killed run self-heal
    // instead of permanently blocking future generations.
    private const string LockFileName = "diagnostics.lock";
    private static readonly TimeSpan StaleLockThreshold = TimeSpan.FromMinutes(30);

    [ExportMethod]
    public void GetProjectDiagnostics()
    {
        try
        {
            var outputRoot = SherlockReportAssets.GetDiagnosticOutputRoot();
            var lockPath = Path.Combine(outputRoot, LockFileName);

            if (File.Exists(lockPath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(lockPath) < StaleLockThreshold)
            {
                Log.Warning(DiagnosticTag, "Diagnostics generation is already running; ignoring new request.");
                return;
            }

            File.WriteAllText(lockPath, DateTime.UtcNow.ToString("o"));
            Log.Info(DiagnosticTag, "Starting project analysis generation...");
            var task = new LongRunningTask(() => RunDiagnosticsGeneration(lockPath), LogicObject);
            task.Start();
        }
        catch (Exception ex)
        {
            Log.Error(DiagnosticTag, $"Failed to start analysis: {ex.Message}");
        }
    }

    private void RunDiagnosticsGeneration(string lockPath)
    {
        try
        {
            AnalyzeProject();
        }
        finally
        {
            try
            {
                File.Delete(lockPath);
            }
            catch (Exception ex)
            {
                Log.Warning(DiagnosticTag, $"Could not remove diagnostics lock file: {ex.Message}");
            }
        }
    }

    private void AnalyzeProject()
    {
        _projectCategoryByNodeId = null;
        var totalWatch = Stopwatch.StartNew();

        var metrics = new ProjectMetrics();
        var project = Project.Current;

        // 0) Pre-scan NetSolution sources for BaseNetLogic-derived class names, needed by
        // ResolveNetLogicTypeName before any node traversal happens below.
        ScanNetLogicClassNames(metrics);

        // 1) Locate the UI category folder
        Log.Info(DiagnosticTag, "[1/5] Parsing UI category...");
        var uiFolder = FindNodeByBrowseName(project, "UI");
        if (uiFolder == null)
        {
            Log.Warning(DiagnosticTag, "UI folder not found. Report will include only global stats.");
        }
        else
        {
            metrics.UIRootName = uiFolder.BrowseName;

            // 2) Traverse UI subtree for all UI metrics
            var uiWatch = Stopwatch.StartNew();
            TraverseUICategory(uiFolder, metrics, uiFolder.BrowseName, 0, null, new Stack<string>());
            uiWatch.Stop();

            // 3) Post-process type usage, interdependencies, and type catalog details
            Log.Info(DiagnosticTag, "[2/5] Building type catalog and interdependencies...");
            BuildTypeUsageMap(metrics);
            BuildInterdependencyMap(metrics);
            BuildTypeCatalogDetails(metrics);
        }

        // 4) Project-wide dynamic links scan (not limited to UI subtree)
        Log.Info(DiagnosticTag, "[3/5] Parsing project-wide dynamic links...");
        CollectProjectDynamicLinks(metrics);

        // 4b) Model category deep-dive (custom types, object roots, data-type usage)
        Log.Info(DiagnosticTag, "Parsing Model category...");
        AnalyzeModelCategory(metrics);

        // 4c) Converters category: reusable converters and where they are used.
        AnalyzeConvertersCategory(metrics);

        // 4d) Alarms category: custom alarm types and configured alarm instances, with their
        // upstream source bindings (reusing the project-wide dynamic link scan).
        AnalyzeAlarmsCategory(metrics);

        // 4e) Recipes / DataStores / Loggers: RecipeSchema catalog, SQLiteStore tables, and
        // DataLogger bindings (Store NodePointers + VariablesToLog dynamic links reuse the
        // same project-wide scans as Alarms).
        AnalyzeRecipesCategory(metrics);
        AnalyzeDataStoresCategory(metrics);
        AnalyzeLoggersCategory(metrics);

        // 4f) Security: Users/Groups/Roles catalogs with incoming cross-references.
        AnalyzeSecurityCategory(metrics);

        // 4g) Translations: per-locale completion stats (translated/missing keys) for every
        // LocalizationDictionary.
        AnalyzeTranslationsCategory(metrics);

        // 4h) Reports / Retentivity: generic catalogs (these categories have no fixed
        // Types/Instances shape in the framework).
        AnalyzeReportsCategory(metrics);
        AnalyzeRetentivityCategory(metrics);

        // 4i) MQTT: Broker + Client(s) with their Publishers/Subscribers hierarchy.
        AnalyzeMqttCategory(metrics);

        // 5) Solution-level C# analysis (impact hotspots)
        Log.Info(DiagnosticTag, "[4/5] Analyzing NetSolution C# sources...");
        AnalyzeSolutionCode(metrics);

        // 6) Data-only output: keep HTML editable, regenerate only data/assets
        Log.Info(DiagnosticTag, "[5/5] Generating report data assets...");
        SaveReportDataOnly(metrics);

        totalWatch.Stop();
        Log.Info(DiagnosticTag, $"Project analysis complete in {totalWatch.Elapsed.TotalSeconds} s");
    }

    private IUANode FindNodeByBrowseName(IUANode root, string browseName)
    {
        if (root == null)
            return null;

        if (string.Equals(root.BrowseName, browseName, StringComparison.Ordinal))
            return root;

        foreach (var child in root.Children)
        {
            var found = FindNodeByBrowseName(child, browseName);
            if (found != null)
                return found;
        }

        return null;
    }

    // A node counts as a "screen" when it is a screen OBJECT/instance or a screen
    // TYPE definition, including anything derived from Screen (e.g. custom base
    // screens such as BaseScreen and screens that derive from it). Optix keeps two
    // parallel managed hierarchies that are NOT mutually assignable:
    //   instances:  Screen -> Panel -> Container -> Item -> BaseUIObject
    //   type defs:  ScreenType -> PanelType -> ContainerType -> ItemType -> ...
    // Custom screen types are MapType-mapped onto the instance hierarchy
    // (e.g. OvensOverview : BaseScreen : Screen), so checking only ScreenType
    // would miss them. Check both hierarchies.
    private static bool IsScreenNode(IUANode node)
    {
        return node is FTOptix.UI.Screen || node is FTOptix.UI.ScreenType;
    }

    private void TraverseUICategory(IUANode node, ProjectMetrics metrics, string currentPath, int depth, ScreenInfo currentScreen, Stack<string> activeDependencyStack)
    {
        if (node == null)
            return;

        metrics.TotalNodes++;
        metrics.UITotalNodes++;
        metrics.MaxUIDepth = Math.Max(metrics.MaxUIDepth, depth);

        // Track UI folders (category internal structure)
        if (node.GetType().Name.Contains("Folder", StringComparison.OrdinalIgnoreCase))
            metrics.UIFolderCount++;

        // Track all object types in UI category
        var typeName = node.GetType().Name;
        Increment(metrics.UITypeCounts, typeName);

        // Track derived-type style information by recording base type chain
        var inheritanceDepth = GetInheritanceDepth(node.GetType());
        if (inheritanceDepth > 0)
            metrics.DerivedObjectCount++;
        metrics.MaxInheritanceDepth = Math.Max(metrics.MaxInheritanceDepth, inheritanceDepth);

        // Count UI object nodes specifically
        if (node is FTOptix.UI.BaseUIObject || node is FTOptix.UI.BaseUIObjectType)
            metrics.UIObjectNodes++;

        // Screen root detection: match screen objects/instances AND screen type
        // definitions, including anything derived from Screen (custom base screens).
        if (IsScreenNode(node))
        {
            currentScreen = new ScreenInfo
            {
                Name = node.BrowseName,
                FullPath = currentPath,
                BaseType = SafeBaseType(node.GetType()),
                DeclaredType = node.GetType().Name,
                RootDepth = depth
            };
            metrics.Screens[node.NodeId.ToString()] = currentScreen;
        }

        bool pushedDependency = false;

        // Catalog custom UI types even when they live OUTSIDE any screen: the type
        // DEFINITIONS themselves (e.g. a widget Object Type under UI) and instances
        // placed directly in the UI tree (not inside a ScreenType). Without this,
        // a project with no screens — or with reusable widgets used outside screens —
        // would show an empty "types" catalog in the UI tab.
        RegisterStandaloneCustomType(metrics, node, currentScreen, currentPath);

        // Accumulate per-screen metrics when inside a screen subtree
        if (currentScreen != null)
        {
            var customTypeRef = TryGetCustomUIType(node);
            var customType = customTypeRef?.BrowseName;
            if (!string.IsNullOrWhiteSpace(customType))
            {
                MarkDependency(currentScreen, metrics, customType, currentScreen.DependencyTypeNamespaces.ContainsKey(customType)
                    ? currentScreen.DependencyTypeNamespaces[customType]
                    : string.Empty, "ui", false, currentPath, customTypeRef);
                Increment(currentScreen.DependencyTypeCounts, customType);

                if (!currentScreen.DependencyTypeNamespaces.ContainsKey(customType))
                {
                    currentScreen.DependencyTypeNamespaces[customType] = customTypeRef != null
                        ? TryGetNamespaceUri(customTypeRef)
                        : string.Empty;

                    if (customTypeRef != null)
                        ExpandScreenDependenciesRecursive(currentScreen, metrics, customTypeRef, currentPath);
                }

                activeDependencyStack.Push(customType);
                pushedDependency = true;
            }

            if (!string.IsNullOrWhiteSpace(customType))
                currentScreen.CustomTypedNodeCount++;
            else
                currentScreen.BaseTypedNodeCount++;

            currentScreen.NodeCount++;
            var relativeDepth = Math.Max(0, depth - currentScreen.RootDepth);
            currentScreen.MaxDepth = Math.Max(currentScreen.MaxDepth, relativeDepth);
            Increment(currentScreen.ElementTypes, typeName);

            if (inheritanceDepth > 0)
                currentScreen.DerivedNodeCount++;

            if (activeDependencyStack.Count > 0)
                Increment(currentScreen.DependencyNodeCounts, activeDependencyStack.Peek());

            // Dynamic links only apply to variables/properties
            if (node is IUAVariable variable)
            {
                var hasDynamicLink = TrackDynamicLink(variable, currentScreen, metrics);
                if (hasDynamicLink && activeDependencyStack.Count > 0)
                    Increment(currentScreen.DependencyDynamicLinkCounts, activeDependencyStack.Peek());

                var dataTypeNode = TryGetCustomDataTypeNode(variable);
                var dataTypeName = dataTypeNode?.BrowseName;
                if (!string.IsNullOrWhiteSpace(dataTypeName))
                {
                    var dtNs = TryGetNamespaceUri(dataTypeNode);
                    MarkDependency(currentScreen, metrics, dataTypeName, dtNs, "datatype", true, currentPath, dataTypeNode);
                    Increment(currentScreen.DependencyTypeCounts, dataTypeName);

                    var dataTypeNodeWeight = EstimateTypeContentNodeWeight(metrics, dataTypeNode);
                    AddCount(currentScreen.DependencyNodeCounts, dataTypeName, dataTypeNodeWeight);

                    if (hasDynamicLink)
                        Increment(currentScreen.DependencyDynamicLinkCounts, dataTypeName);
                }
            }
        }

        // Recurse
        foreach (var child in node.Children)
        {
            var childPath = string.Concat(currentPath, "/", child.BrowseName);
            TraverseUICategory(child, metrics, childPath, depth + 1, currentScreen, activeDependencyStack);
        }

        if (pushedDependency)
            activeDependencyStack.Pop();
    }

    private string TryGetNamespaceUri(IUANode node)
    {
        try
        {
            if (node == null)
                return string.Empty;

            var context = node.Context ?? LogicObject?.Context;
            if (context == null)
                return string.Empty;

            // Prefer direct API call when available
            try
            {
                var uri = context.GetNamespaceUri(node.NodeId.NamespaceIndex);
                if (!string.IsNullOrWhiteSpace(uri))
                    return uri;
            }
            catch
            {
                // Fall back to reflection-based lookup below
            }

            // Resolve namespace URI through runtime context without hard dependencies
            var method = context.GetType().GetMethod("GetNamespaceUri", new[] { typeof(int) });
            if (method != null)
                return method.Invoke(context, new object[] { node.NodeId.NamespaceIndex }) as string ?? string.Empty;

            method = context.GetType().GetMethod("GetNamespaceUri", new[] { typeof(ushort) });
            if (method != null)
                return method.Invoke(context, new object[] { (ushort)node.NodeId.NamespaceIndex }) as string ?? string.Empty;

            return string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool IsCustomUIType(IUAObjectType objectType)
    {
        if (objectType == null)
            return false;

        if (ShouldIgnoreDependencyTypeName(objectType.BrowseName))
            return false;

        try
        {
            var namespaceUri = TryGetNamespaceUri(objectType);
            if (string.IsNullOrWhiteSpace(namespaceUri))
                return false;

            // Base/system types are in FTOptix namespaces (e.g. FTOptix:UI)
            return namespaceUri.IndexOf("FTOptix", StringComparison.OrdinalIgnoreCase) < 0;
        }
        catch
        {
            return false;
        }
    }

    private IUAObjectType TryGetCustomUIType(IUANode node)
    {
        try
        {
            if (node == null)
                return null;

            if (node is IUAObject uaObject)
            {
                var objectType = uaObject.ObjectType;
                if (objectType is FTOptix.UI.BaseUIObjectType && IsCustomUIType(objectType))
                    return objectType;
            }

            var refs = node.Refs.GetReferences();
            foreach (var reference in refs)
            {
                if (reference.TargetNode is IUAObjectType objectType && objectType is FTOptix.UI.BaseUIObjectType)
                {
                    if (IsCustomUIType(objectType))
                        return objectType;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool ShouldIgnoreDependencyTypeName(string browseName)
    {
        if (string.IsNullOrWhiteSpace(browseName))
            return true;

        return string.Equals(browseName, "MainWindow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(browseName, "BaseObjectType", StringComparison.OrdinalIgnoreCase);
    }

    private void MarkDependency(ScreenInfo screen, ProjectMetrics metrics, string depName, string namespaceUri, string kind, bool createZeroCounters, string usagePath, IUANode typeNode)
    {
        if (screen == null || ShouldIgnoreDependencyTypeName(depName))
            return;

        // A screen type should never appear as its own dependency
        if (string.Equals(depName, screen.Name, StringComparison.OrdinalIgnoreCase))
            return;

        screen.Dependencies.Add(depName);

        // "ui" kind takes priority: never downgrade a UI dep to datatype
        if (!screen.DependencyKinds.TryGetValue(depName, out var existingKind)
            || GetDependencyKindPriority(kind) > GetDependencyKindPriority(existingKind))
            screen.DependencyKinds[depName] = kind;

        RegisterTypeUsage(metrics, screen, depName, namespaceUri, kind, usagePath, typeNode);

        if (!string.IsNullOrWhiteSpace(namespaceUri) && !screen.DependencyTypeNamespaces.ContainsKey(depName))
            screen.DependencyTypeNamespaces[depName] = namespaceUri;

        if (createZeroCounters)
        {
            EnsureKey(screen.DependencyTypeCounts, depName);
            EnsureKey(screen.DependencyNodeCounts, depName);
            EnsureKey(screen.DependencyDynamicLinkCounts, depName);
        }
    }

    private string GetCustomUITypeName(IUANode node)
    {
        var objectType = TryGetCustomUIType(node);
        return objectType?.BrowseName;
    }

    private int GetDependencyKindPriority(string kind)
    {
        switch (kind)
        {
            case "ui":
                return 3;
            case "datatype":
                return 2;
            case "converter":
                return 2;
            case "object":
                return 1;
            default:
                return 0;
        }
    }

    private void RegisterStandaloneCustomType(ProjectMetrics metrics, IUANode node, ScreenInfo currentScreen, string usagePath)
    {
        if (metrics == null || node == null)
            return;

        // Case 1: the node IS a custom UI Object Type definition (e.g. a widget
        // type under UI). Register it so it appears in the catalog with its content
        // and inter-type dependency analysis, regardless of whether it is ever
        // instantiated inside a screen.
        if (node is IUAObjectType objectType
            && objectType is FTOptix.UI.BaseUIObjectType
            && IsCustomUIType(objectType))
        {
            RegisterTypeUsage(metrics, null, objectType.BrowseName,
                TryGetNamespaceUri(objectType), "ui", null, objectType);
            return;
        }

        // Case 2: a custom-typed instance placed OUTSIDE any screen subtree. The
        // in-screen path already records usage; here we only handle the standalone
        // case so usage counts are not lost for screen-less projects.
        if (currentScreen == null)
        {
            var customTypeRef = TryGetCustomUIType(node);
            if (customTypeRef != null && !ShouldIgnoreDependencyTypeName(customTypeRef.BrowseName))
                RegisterTypeUsage(metrics, null, customTypeRef.BrowseName,
                    TryGetNamespaceUri(customTypeRef), "ui", usagePath, customTypeRef);
        }
    }

    private void RegisterTypeUsage(ProjectMetrics metrics, ScreenInfo screen, string depName, string namespaceUri, string kind, string usagePath, IUANode typeNode)
    {
        if (metrics == null || string.IsNullOrWhiteSpace(depName))
            return;

        if (!metrics.TypeCatalog.TryGetValue(depName, out var typeInfo))
        {
            typeInfo = new TypeMapInfo
            {
                Name = depName,
                Kind = kind,
                NamespaceUri = namespaceUri,
                TypeNode = typeNode
            };
            metrics.TypeCatalog[depName] = typeInfo;
        }
        else
        {
            if (GetDependencyKindPriority(kind) > GetDependencyKindPriority(typeInfo.Kind))
                typeInfo.Kind = kind;

            if (string.IsNullOrWhiteSpace(typeInfo.NamespaceUri) && !string.IsNullOrWhiteSpace(namespaceUri))
                typeInfo.NamespaceUri = namespaceUri;

            if (typeInfo.TypeNode == null && typeNode != null)
                typeInfo.TypeNode = typeNode;
        }

        if (!string.IsNullOrWhiteSpace(usagePath))
            typeInfo.UsagePaths.Add(usagePath);

        if (screen != null && !string.IsNullOrWhiteSpace(screen.Name))
            typeInfo.ScreenNames.Add(screen.Name);
    }

    private IUANode TryGetCustomDataTypeNode(IUAVariable variable)
    {
        try
        {
            if (variable == null)
                return null;

            var dataTypeNodeId = variable.DataType;
            if (dataTypeNodeId == null || dataTypeNodeId.NamespaceIndex == 0)
                return null;

            IUANode dataTypeNode;
            try
            {
                dataTypeNode = InformationModel.Get(dataTypeNodeId);
            }
            catch
            {
                return null;
            }

            if (dataTypeNode == null || ShouldIgnoreDependencyTypeName(dataTypeNode.BrowseName))
                return null;

            var namespaceUri = TryGetNamespaceUri(dataTypeNode);
            if (string.IsNullOrWhiteSpace(namespaceUri))
                return null;

            if (namespaceUri.IndexOf("FTOptix", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            return dataTypeNode;
        }
        catch
        {
            return null;
        }
    }

    private string GetCustomDataTypeName(IUAVariable variable)
    {
        return TryGetCustomDataTypeNode(variable)?.BrowseName;
    }

    private string GetDataTypeNamespaceUri(IUAVariable variable)
    {
        try
        {
            var context = variable?.Context ?? LogicObject?.Context;
            if (context == null || variable?.DataType == null)
                return string.Empty;

            return context.GetNamespaceUri(variable.DataType.NamespaceIndex) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void EnsureKey(Dictionary<string, int> dict, string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!dict.ContainsKey(key))
            dict[key] = 0;
    }

    private void AddCount(Dictionary<string, int> dict, string key, int delta)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!dict.ContainsKey(key))
            dict[key] = 0;

        dict[key] += Math.Max(0, delta);
    }

    private int EstimateTypeContentNodeWeight(ProjectMetrics metrics, IUANode typeNode)
    {
        if (typeNode == null)
            return 1;

        var key = typeNode.NodeId.ToString();
        if (metrics.TypeNodeWeightCache.TryGetValue(key, out var cached))
            return cached;

        var visited = new HashSet<string>();
        int count = CountTypeNodesRecursive(typeNode, visited);
        if (count <= 0)
            count = 1;

        metrics.TypeNodeWeightCache[key] = count;
        return count;
    }

    private int CountTypeNodesRecursive(IUANode node, HashSet<string> visited)
    {
        if (node == null)
            return 0;

        var key = node.NodeId.ToString();
        if (!visited.Add(key))
            return 0;

        int count = 1;
        foreach (var child in node.Children)
            count += CountTypeNodesRecursive(child, visited);

        return count;
    }

    private void ExpandScreenDependenciesRecursive(ScreenInfo screen, ProjectMetrics metrics, IUAObjectType rootType, string usagePath)
    {
        if (screen == null || rootType == null)
            return;

        MarkDependency(screen, metrics, rootType.BrowseName, TryGetNamespaceUri(rootType), "ui", true, usagePath, rootType);

        var key = rootType.NodeId.ToString();
        if (!screen.ExpandedDependencyTypeNodeIds.Add(key))
            return;

        // Add parent chain up to base Screen (include custom parents, stop at Screen).
        var parent = rootType.SuperType;
        int guard = 0;
        while (parent != null && guard++ < 64)
        {
            var parentName = parent.BrowseName;
            if (string.Equals(parentName, "Screen", StringComparison.OrdinalIgnoreCase))
                break;

            if (IsCustomUIType(parent))
            {
                MarkDependency(screen, metrics, parentName, TryGetNamespaceUri(parent), "ui", true, usagePath, parent);

                ExpandScreenDependenciesRecursive(screen, metrics, parent, usagePath);
            }

            parent = parent.SuperType;
        }

        // Add dependencies carried by this type (recursive children typed by custom UI object types).
        AddCarriedDependenciesFromNode(screen, metrics, rootType, usagePath);
    }

    private void AddCarriedDependenciesFromNode(ScreenInfo screen, ProjectMetrics metrics, IUANode root, string usagePath)
    {
        if (screen == null || root == null)
            return;

        foreach (var child in root.Children)
        {
            var childType = TryGetCustomUIType(child);
            if (childType != null)
            {
                var depName = childType.BrowseName;
                MarkDependency(screen, metrics, depName, TryGetNamespaceUri(childType), "ui", true, usagePath, childType);

                ExpandScreenDependenciesRecursive(screen, metrics, childType, usagePath);
            }

            if (child is IUAVariable childVar)
            {
                var dataTypeNode = TryGetCustomDataTypeNode(childVar);
                var dataTypeName = dataTypeNode?.BrowseName;
                if (!string.IsNullOrWhiteSpace(dataTypeName))
                {
                    var dtNs = TryGetNamespaceUri(dataTypeNode);
                    MarkDependency(screen, metrics, dataTypeName, dtNs, "datatype", true, usagePath, dataTypeNode);
                }
            }

            AddCarriedDependenciesFromNode(screen, metrics, child, usagePath);
        }
    }

    private bool HasDynamicLink(IUAVariable variable)
    {
        try
        {
            return variable?.Refs.GetVariable(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink) != null;
        }
        catch
        {
            return false;
        }
    }

    private bool TrackDynamicLink(IUAVariable variable, ScreenInfo screen, ProjectMetrics metrics)
    {
        try
        {
            var dynamicLinkVar = variable.Refs.GetVariable(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink);
            if (dynamicLinkVar == null)
                return false;

            var targetPath = dynamicLinkVar.Value?.ToString() ?? string.Empty;
            var nodeKey = string.Concat(screen.Name, "::", variable.BrowseName, "::", variable.NodeId.ToString());

            screen.DynamicLinkCount++;
            metrics.TotalDynamicLinks++;

            bool hasConverter = ContainsConverter(variable);
            if (hasConverter)
            {
                screen.ComplexDynamicLinkCount++;
                metrics.TotalComplexDynamicLinks++;
            }

            var resultKind = ClassifyDynamicLinkResult(variable, targetPath);
            if (string.Equals(resultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal))
            {
                screen.BrokenDynamicLinkCount++;
                metrics.TotalBrokenDynamicLinks++;
            }

            if (!string.IsNullOrWhiteSpace(targetPath))
            {
                Increment(screen.DynamicLinkTargetCounts, targetPath);
                Increment(metrics.GlobalDynamicLinkTargetCounts, targetPath);

                if (screen.DynamicLinkTargetCounts[targetPath] > 1)
                {
                    screen.RepeatedTargetDynamicLinks++;
                }
            }

            screen.DynamicLinkDetails.Add(new DynamicLinkInfo
            {
                ScreenName = screen.Name,
                SourceNode = nodeKey,
                SourcePath = SafeNodePath(variable),
                TargetPath = targetPath,
                IsComplex = hasConverter,
                ResultKind = resultKind
            });

            return true;
        }
        catch
        {
            // Best effort: one bad variable should not abort report generation.
            return false;
        }
    }

    private const string DynamicLinkResultKindOk = "ok";
    private const string DynamicLinkResultKindBroken = "broken";
    // Retained only so legacy metric counters compile; ClassifyDynamicLinkResult never returns it anymore
    // (runtime-resolved links are OK), so every "unresolved" count naturally stays 0.
    private const string DynamicLinkResultKindUnresolved = "unresolved";

    private string ClassifyDynamicLinkResult(IUANode contextNode, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            return DynamicLinkResultKindOk;

        // Alias placeholders {..}, NodePointer and @NodeId bindings are resolved at runtime -> valid, not attention points.
        if (IsRuntimeResolvedDynamicLinkPath(targetPath))
            return DynamicLinkResultKindOk;

        try
        {
            var resolved = LogicObject?.Context?.ResolvePath(contextNode, targetPath);
            return resolved == null
                ? DynamicLinkResultKindBroken
                : DynamicLinkResultKindOk;
        }
        catch
        {
            return DynamicLinkResultKindBroken;
        }
    }

    private IUANode TryExtractResolvedNode(object resolvePathResult)
    {
        if (resolvePathResult == null)
            return null;

        if (resolvePathResult is IUANode directNode)
            return directNode;

        try
        {
            var resultType = resolvePathResult.GetType();
            var candidateProps = new[] { "TargetNode", "Node", "ResolvedNode", "Value" };
            foreach (var propName in candidateProps)
            {
                var prop = resultType.GetProperty(propName);
                if (prop == null)
                    continue;

                var value = prop.GetValue(resolvePathResult);
                if (value is IUANode candidateNode)
                    return candidateNode;
            }
        }
        catch
        {
            // Best effort for cross-reference enrichment.
        }

        return null;
    }

    // ---- Model category deep-dive -------------------------------------------

    // The Model category folder is a DIRECT folder-like child of the project root
    // (same rule GetOrBuildProjectCategoryMap uses). A plain recursive name search
    // would wrongly match a nested pointer/property also called "Model".
    private IUANode FindModelCategoryFolder()
    {
        var project = Project.Current;
        if (project == null)
            return null;
        try
        {
            foreach (var child in project.Children)
            {
                if (child != null
                    && IsFolderLikeNode(child)
                    && string.Equals(child.BrowseName, "Model", StringComparison.Ordinal))
                    return child;
            }
        }
        catch
        {
            // Fall through.
        }
        return null;
    }

    private void AnalyzeModelCategory(ProjectMetrics metrics)
    {
        var modelFolder = FindModelCategoryFolder();
        if (modelFolder == null)
            return;

        // Full-subtree stats: max depth, data-type distribution and per-type usage.
        WalkModelStats(modelFolder, metrics, 0);

        // Custom type definitions declared under Model (object types / structures / enums).
        CollectModelTypes(modelFolder, metrics);

        // Project-wide usages of those Model types (instances/variables anywhere: Model, UI widgets, ...).
        CollectModelTypeUsages(metrics);

        // Object roots: direct children that hold data (skip the type-definition folder).
        foreach (var child in modelFolder.Children)
        {
            if (child == null)
                continue;
            if (string.Equals(child.BrowseName, "Types", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!(IsObjectLikeNode(child) || IsFolderLikeNode(child)))
                continue;

            var info = new ModelObjectInfo
            {
                Name = child.BrowseName,
                Path = BuildTypeLocationPath(child),
                TypeName = ResolveNodeTypeName(child)
            };
            WalkModelObjectRoot(child, info, 0, metrics);
            metrics.ModelObjects.Add(info);
        }

        metrics.ModelObjects = metrics.ModelObjects
            .OrderByDescending(o => o.NodeCount)
            .ToList();

        // Recursive Model tree for the Overview: scalar variables are leaves, objects/
        // structures expand to their children down to scalars. Custom-typed object
        // instances are excluded (their layout belongs to the type -> Data Types tab).
        metrics.ModelTree.Clear();
        foreach (var child in modelFolder.Children)
        {
            if (child == null)
                continue;
            if (string.Equals(child.BrowseName, "Types", StringComparison.OrdinalIgnoreCase))
                continue;
            var treeNode = BuildModelTreeNode(child, metrics);
            if (treeNode != null)
                metrics.ModelTree.Add(treeNode);
        }
    }

    // Reusable converters defined under the project's "Converters" folder, and where each
    // one is used (nodes whose HasConverter reference points at it). We assume these are all
    // shared converter definitions (no instances), per the current project shape.
    private void AnalyzeConvertersCategory(ProjectMetrics metrics)
    {
        metrics.ConverterCatalog.Clear();
        metrics.ConverterOtherItems.Clear();
        var project = Project.Current;
        if (project == null)
            return;

        IUANode convFolder = null;
        try
        {
            foreach (var child in project.Children)
            {
                if (child != null && string.Equals(child.BrowseName, "Converters", StringComparison.Ordinal))
                {
                    convFolder = child;
                    break;
                }
            }
        }
        catch
        {
            return;
        }
        if (convFolder == null)
            return;

        CollectConverters(convFolder, metrics);

        metrics.ConverterCatalog = metrics.ConverterCatalog
            .OrderByDescending(c => c.UsageCount)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (metrics.ConverterOtherItems.Count > 0)
        {
            ComputeOtherItemUsages(metrics.ConverterOtherItems, metrics);
            metrics.ConverterOtherItems = metrics.ConverterOtherItems
                .OrderByDescending(c => c.UsageCount)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void CollectConverters(IUANode node, ProjectMetrics metrics)
    {
        if (node == null)
            return;

        foreach (var child in node.Children)
        {
            if (child == null)
                continue;

            // Converter TYPE definition = a custom object type deriving from a framework converter
            // (ValueMapConverter / ExpressionEvaluator / ...). NOT an instance. We do not descend
            // into it (its internals may contain helper converters).
            if (child is IUAObjectType convType && !IsFrameworkType(child))
            {
                var info = new ConverterCatalogInfo
                {
                    Name = child.BrowseName,
                    TypeName = convType.SuperType != null ? convType.SuperType.BrowseName : ResolveNodeTypeName(child),
                    Path = SafeNodePath(child),
                    Kind = "Converter"
                };
                var key = CanonicalRefKey(info.Path);
                if (!string.IsNullOrEmpty(key)
                    && metrics.ConverterUsagesByPath.TryGetValue(key, out var usages)
                    && usages != null)
                {
                    info.UsageCount = usages.Count;
                    info.UsagePaths = usages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
                }
                metrics.ConverterCatalog.Add(info);
            }
            // Non-converter type definitions (Enumerations, other DataTypes, VariableTypes) placed
            // under the Converters folder. These conceptually belong to Model, but since the project
            // physically stores them here we surface them in an "Other" bucket instead of hiding them.
            else if ((child is IUADataType || child is IUAVariableType) && !IsFrameworkType(child))
            {
                var info = new ConverterCatalogInfo
                {
                    Name = child.BrowseName,
                    TypeName = ResolveNodeTypeName(child),
                    Path = SafeNodePath(child),
                    Kind = ClassifyModelTypeKind(child)
                };
                metrics.ConverterOtherItems.Add(info);
            }
            else if (IsFolderLikeNode(child))
            {
                CollectConverters(child, metrics);
            }
        }
    }

    // Computes project-wide usage (instance/reference matches + "used as kind" NodePointer refs)
    // for a small set of standalone type items, mirroring CollectModelTypeUsages but for types that
    // live outside the Model/Types subtree (e.g. an enum placed under Converters).
    private void ComputeOtherItemUsages(List<ConverterCatalogInfo> items, ProjectMetrics metrics)
    {
        if (items == null || items.Count == 0)
            return;

        var byName = new Dictionary<string, ConverterCatalogInfo>(StringComparer.Ordinal);
        foreach (var t in items)
        {
            if (!string.IsNullOrWhiteSpace(t.Name) && !byName.ContainsKey(t.Name))
                byName[t.Name] = t;
        }

        var seen = new Dictionary<ConverterCatalogInfo, HashSet<string>>();

        IEnumerable<IUANode> projectNodes;
        try
        {
            projectNodes = Project.Current.Parent.FindNodesByType<IUANode>();
        }
        catch
        {
            projectNodes = null;
        }

        if (projectNodes != null)
        {
            foreach (var node in projectNodes)
            {
                if (node == null)
                    continue;

                string typeName = null;
                if (node is IUAObject obj && obj.ObjectType != null)
                    typeName = obj.ObjectType.BrowseName;
                else if (node is IUAVariable variable)
                    typeName = ResolveVariableDataTypeName(variable);

                if (string.IsNullOrWhiteSpace(typeName) || !byName.TryGetValue(typeName, out var t))
                    continue;

                if (!seen.TryGetValue(t, out var set))
                {
                    set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    seen[t] = set;
                }

                var path = SafeNodePath(node);
                if (set.Add(path))
                {
                    t.UsageCount++;
                    if (t.UsagePaths.Count < 200)
                        t.UsagePaths.Add(path);
                }
            }
        }

        foreach (var t in items)
        {
            var typeKey = CanonicalRefKey(t.Path);
            if (string.IsNullOrEmpty(typeKey)
                || !metrics.IncomingRefsByPath.TryGetValue(typeKey, out var kindRefs)
                || kindRefs == null)
                continue;

            if (!seen.TryGetValue(t, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seen[t] = set;
            }

            foreach (var r in kindRefs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (set.Add(r))
                {
                    t.UsageCount++;
                    if (t.UsagePaths.Count < 200)
                        t.UsagePaths.Add(r);
                }
            }
        }
    }

    private string GenerateConverterCatalogJson(List<ConverterCatalogInfo> items, int max)
    {
        var list = (items ?? new List<ConverterCatalogInfo>()).Take(max).ToList();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(c.Name));
            sb.Append("\",\"type\":\""); sb.Append(EscapeJson(c.TypeName ?? string.Empty));
            sb.Append("\",\"kind\":\""); sb.Append(EscapeJson(c.Kind ?? "Converter"));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(c.Path ?? string.Empty));
            sb.Append("\",\"usageCount\":"); sb.Append(c.UsageCount);
            sb.Append(",\"usagePaths\":[");
            var paths = c.UsagePaths ?? new List<string>();
            for (int u = 0; u < paths.Count; u++)
            {
                if (u > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(paths[u])); sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    // Alarms category: custom alarm ObjectTypes (Alarms/Types) and configured alarm instances
    // (Alarms/Instances), each instance's upstream source bindings (which Model/NetLogic
    // variables feed it) reusing the already-collected project-wide dynamic link scan, plus
    // any incoming references pointing back at the alarm instance itself.
    private void AnalyzeAlarmsCategory(ProjectMetrics metrics)
    {
        metrics.AlarmCatalog.Clear();
        metrics.AlarmTypeCatalog.Clear();
        var project = Project.Current;
        if (project == null)
            return;

        IUANode alarmsFolder = null;
        try
        {
            foreach (var child in project.Children)
            {
                if (child != null && string.Equals(child.BrowseName, "Alarms", StringComparison.Ordinal))
                {
                    alarmsFolder = child;
                    break;
                }
            }
        }
        catch
        {
            return;
        }
        if (alarmsFolder == null)
            return;

        IUANode typesFolder = null;
        IUANode instancesFolder = null;
        foreach (var child in alarmsFolder.Children)
        {
            if (child == null)
                continue;
            if (string.Equals(child.BrowseName, "Types", StringComparison.Ordinal))
                typesFolder = child;
            else if (string.Equals(child.BrowseName, "Instances", StringComparison.Ordinal))
                instancesFolder = child;
        }

        if (typesFolder != null)
        {
            foreach (var child in typesFolder.Children)
            {
                if (child is IUAObjectType alarmType && !IsFrameworkType(child))
                {
                    metrics.AlarmTypeCatalog.Add(new ConverterCatalogInfo
                    {
                        Name = child.BrowseName,
                        TypeName = alarmType.SuperType != null ? alarmType.SuperType.BrowseName : ResolveNodeTypeName(child),
                        Path = SafeNodePath(child),
                        Kind = "Alarm Type"
                    });
                }
            }
        }

        if (instancesFolder != null)
        {
            foreach (var child in instancesFolder.Children)
            {
                if (child == null || !(child is IUAObject))
                    continue;

                var info = new AlarmInstanceInfo
                {
                    Name = child.BrowseName,
                    TypeName = ResolveNodeTypeName(child),
                    Path = SafeNodePath(child)
                };

                foreach (var field in child.Children)
                {
                    if (field == null)
                        continue;
                    if (field is IUAVariable fieldVar && string.Equals(field.BrowseName, "Severity", StringComparison.Ordinal))
                        info.Severity = fieldVar.Value?.ToString() ?? string.Empty;
                    else if (field is IUAVariable msgVar && string.Equals(field.BrowseName, "Message", StringComparison.Ordinal))
                        info.Message = FormatLocalizedTextValue(msgVar.Value);
                }

                var prefix = (info.Path ?? string.Empty).TrimEnd('/') + "/";
                foreach (var link in metrics.ProjectDynamicLinkDetails)
                {
                    if (string.IsNullOrEmpty(link.SourcePath) || !link.SourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    info.SourceLinks.Add(new AlarmSourceLinkInfo
                    {
                        FieldName = link.SourcePath.Substring(prefix.Length),
                        Target = link.TargetPath,
                        ResultKind = link.ResultKind
                    });
                }

                var key = CanonicalRefKey(info.Path);
                if (!string.IsNullOrEmpty(key)
                    && metrics.IncomingRefsByPath.TryGetValue(key, out var incoming)
                    && incoming != null)
                    info.IncomingRefs = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(100).ToList();

                metrics.AlarmCatalog.Add(info);
            }
        }

        var instancesByType = metrics.AlarmCatalog
            .GroupBy(a => a.TypeName ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        foreach (var t in metrics.AlarmTypeCatalog)
        {
            if (!string.IsNullOrEmpty(t.Name) && instancesByType.TryGetValue(t.Name, out var instances))
            {
                t.UsageCount = instances.Count;
                t.UsagePaths = instances.Select(a => a.Path).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
            }
        }

        metrics.AlarmCatalog = metrics.AlarmCatalog
            .OrderByDescending(a => a.SourceLinks.Count)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        metrics.AlarmTypeCatalog = metrics.AlarmTypeCatalog
            .OrderByDescending(t => t.UsageCount)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GenerateAlarmCatalogJson(List<AlarmInstanceInfo> items, int max)
    {
        var list = (items ?? new List<AlarmInstanceInfo>()).Take(max).ToList();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
            var a = list[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(a.Name));
            sb.Append("\",\"type\":\""); sb.Append(EscapeJson(a.TypeName ?? string.Empty));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(a.Path ?? string.Empty));
            sb.Append("\",\"severity\":\""); sb.Append(EscapeJson(a.Severity ?? string.Empty));
            sb.Append("\",\"message\":\""); sb.Append(EscapeJson(a.Message ?? string.Empty));
            sb.Append("\",\"sourceLinks\":[");
            var links = a.SourceLinks ?? new List<AlarmSourceLinkInfo>();
            for (int s = 0; s < links.Count; s++)
            {
                if (s > 0) sb.Append(",");
                var l = links[s];
                sb.Append("{\"field\":\""); sb.Append(EscapeJson(l.FieldName ?? string.Empty));
                sb.Append("\",\"target\":\""); sb.Append(EscapeJson(l.Target ?? string.Empty));
                sb.Append("\",\"resultKind\":\""); sb.Append(EscapeJson(l.ResultKind ?? string.Empty));
                sb.Append("\"}");
            }
            sb.Append("],\"incomingRefs\":[");
            var refs = a.IncomingRefs ?? new List<string>();
            for (int r = 0; r < refs.Count; r++)
            {
                if (r > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(refs[r])); sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private IUANode FindProjectCategoryFolder(string browseName)
    {
        var project = Project.Current;
        if (project == null)
            return null;
        try
        {
            foreach (var child in project.Children)
            {
                if (child != null && string.Equals(child.BrowseName, browseName, StringComparison.Ordinal))
                    return child;
            }
        }
        catch
        {
            // Fall through.
        }
        return null;
    }

    // Report header subtitle: project location one folder above "ProjectFiles" (i.e. the
    // project's own folder), not the raw ProjectDirectory (which ends in "...\ProjectFiles").
    private string GetDisplayProjectPath()
    {
        var projectDir = Project.Current.ProjectDirectory;
        if (string.IsNullOrWhiteSpace(projectDir))
            return projectDir;

        try
        {
            var dir = new DirectoryInfo(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(dir.Name, "ProjectFiles", StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
                return dir.Parent.FullName;
            return projectDir;
        }
        catch
        {
            return projectDir;
        }
    }

    // Recipes category: RecipeSchema definitions. Each binds a Model target type
    // (TargetNode) and a DataStore (Store) via NodePointer, plus per-item
    // Approved/Used/Deprecated metadata flags. Cross-references reuse the incoming-ref
    // map already built by the project-wide NodePointer/dynamic-link scan.
    private void AnalyzeRecipesCategory(ProjectMetrics metrics)
    {
        metrics.RecipeSchemas.Clear();
        var recipesFolder = FindProjectCategoryFolder("Recipes");
        if (recipesFolder == null)
            return;

        foreach (var child in recipesFolder.Children)
        {
            if (!(child is IUAObject))
                continue;

            var info = new RecipeSchemaInfo
            {
                Name = child.BrowseName,
                Path = SafeNodePath(child)
            };

            foreach (var field in child.Children)
            {
                if (field == null)
                    continue;

                if (field is IUAVariable pointerVar
                    && (string.Equals(field.BrowseName, "TargetNode", StringComparison.Ordinal)
                        || string.Equals(field.BrowseName, "Store", StringComparison.Ordinal)))
                {
                    var target = TryGetNodePointerTargetPath(pointerVar) ?? string.Empty;
                    if (string.Equals(field.BrowseName, "TargetNode", StringComparison.Ordinal))
                        info.TargetNode = target;
                    else
                        info.Store = target;
                }
                else if (field is IUAVariable statusVar && string.Equals(field.BrowseName, "Status", StringComparison.Ordinal))
                {
                    info.Status = statusVar.Value?.Value?.ToString() ?? string.Empty;
                }
                else if (string.Equals(field.BrowseName, "Metadata", StringComparison.Ordinal))
                {
                    foreach (var flag in field.Children)
                    {
                        if (flag is IUAVariable flagVar
                            && bool.TryParse(flagVar.Value?.Value?.ToString(), out var flagOn) && flagOn)
                            info.MetadataFlags.Add(flag.BrowseName);
                    }
                }
            }

            var key = CanonicalRefKey(info.Path);
            if (!string.IsNullOrEmpty(key)
                && metrics.IncomingRefsByPath.TryGetValue(key, out var incoming) && incoming != null)
                info.IncomingRefs = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(100).ToList();

            metrics.RecipeSchemas.Add(info);
        }

        metrics.RecipeSchemas = metrics.RecipeSchemas
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GenerateRecipeSchemaJson(List<RecipeSchemaInfo> items, int max)
    {
        var list = (items ?? new List<RecipeSchemaInfo>()).Take(max).ToList();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(r.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(r.Path ?? string.Empty));
            sb.Append("\",\"targetNode\":\""); sb.Append(EscapeJson(r.TargetNode ?? string.Empty));
            sb.Append("\",\"store\":\""); sb.Append(EscapeJson(r.Store ?? string.Empty));
            sb.Append("\",\"status\":\""); sb.Append(EscapeJson(r.Status ?? string.Empty));
            sb.Append("\",\"metadata\":[");
            var flags = r.MetadataFlags ?? new List<string>();
            for (int f = 0; f < flags.Count; f++)
            {
                if (f > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(flags[f])); sb.Append("\"");
            }
            sb.Append("],\"incomingRefs\":[");
            var refs = r.IncomingRefs ?? new List<string>();
            for (int u = 0; u < refs.Count; u++)
            {
                if (u > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(refs[u])); sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    // DataStores category: each Store instance (e.g. SQLiteStore) exposes a Tables catalog
    // (name + column count). Incoming refs pick up Store NodePointers from Recipes/Loggers.
    private void AnalyzeDataStoresCategory(ProjectMetrics metrics)
    {
        metrics.DataStoreCatalog.Clear();
        var storesFolder = FindProjectCategoryFolder("DataStores");
        if (storesFolder == null)
            return;

        foreach (var child in storesFolder.Children)
        {
            if (!(child is IUAObject))
                continue;

            var info = new DataStoreInfo
            {
                Name = child.BrowseName,
                Path = SafeNodePath(child),
                TypeName = ResolveNodeTypeName(child)
            };

            IUANode tablesFolder = null;
            foreach (var field in child.Children)
            {
                if (field != null && string.Equals(field.BrowseName, "Tables", StringComparison.Ordinal))
                {
                    tablesFolder = field;
                    break;
                }
            }

            if (tablesFolder != null)
            {
                foreach (var table in tablesFolder.Children)
                {
                    if (table == null)
                        continue;

                    int columnCount = 0;
                    foreach (var tableChild in table.Children)
                    {
                        if (tableChild == null)
                            continue;
                        if (string.Equals(tableChild.BrowseName, "Columns", StringComparison.Ordinal))
                            columnCount += tableChild.Children.Count();
                    }

                    info.Tables.Add(new DataStoreTableInfo { Name = table.BrowseName, ColumnCount = columnCount });
                }
            }

            var key = CanonicalRefKey(info.Path);
            if (!string.IsNullOrEmpty(key)
                && metrics.IncomingRefsByPath.TryGetValue(key, out var incoming) && incoming != null)
                info.IncomingRefs = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(100).ToList();

            metrics.DataStoreCatalog.Add(info);
        }

        metrics.DataStoreCatalog = metrics.DataStoreCatalog
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GenerateDataStoreCatalogJson(List<DataStoreInfo> items, int max)
    {
        var list = (items ?? new List<DataStoreInfo>()).Take(max).ToList();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(d.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(d.Path ?? string.Empty));
            sb.Append("\",\"type\":\""); sb.Append(EscapeJson(d.TypeName ?? string.Empty));
            sb.Append("\",\"tables\":[");
            var tables = d.Tables ?? new List<DataStoreTableInfo>();
            for (int t = 0; t < tables.Count; t++)
            {
                if (t > 0) sb.Append(",");
                sb.Append("{\"name\":\""); sb.Append(EscapeJson(tables[t].Name));
                sb.Append("\",\"columns\":"); sb.Append(tables[t].ColumnCount);
                sb.Append("}");
            }
            sb.Append("],\"incomingRefs\":[");
            var refs = d.IncomingRefs ?? new List<string>();
            for (int u = 0; u < refs.Count; u++)
            {
                if (u > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(refs[u])); sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    // Loggers category: each DataLogger's Store NodePointer + its VariablesToLog bindings,
    // reusing the already-collected project-wide dynamic link scan (same trick as Alarms:
    // prefix-match ProjectDynamicLinkDetails by the logger's path).
    private void AnalyzeLoggersCategory(ProjectMetrics metrics)
    {
        metrics.LoggerCatalog.Clear();
        var loggersFolder = FindProjectCategoryFolder("Loggers");
        if (loggersFolder == null)
            return;

        foreach (var child in loggersFolder.Children)
        {
            if (!(child is IUAObject))
                continue;

            var info = new LoggerInfo
            {
                Name = child.BrowseName,
                Path = SafeNodePath(child)
            };

            foreach (var field in child.Children)
            {
                if (field is IUAVariable pointerVar && string.Equals(field.BrowseName, "Store", StringComparison.Ordinal))
                    info.Store = TryGetNodePointerTargetPath(pointerVar) ?? string.Empty;
            }

            var prefix = (info.Path ?? string.Empty).TrimEnd('/') + "/";
            foreach (var link in metrics.ProjectDynamicLinkDetails)
            {
                if (string.IsNullOrEmpty(link.SourcePath) || !link.SourcePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                info.Variables.Add(new AlarmSourceLinkInfo
                {
                    FieldName = link.SourcePath.Substring(prefix.Length),
                    Target = link.TargetPath,
                    ResultKind = link.ResultKind
                });
            }

            var key = CanonicalRefKey(info.Path);
            if (!string.IsNullOrEmpty(key)
                && metrics.IncomingRefsByPath.TryGetValue(key, out var incoming) && incoming != null)
                info.IncomingRefs = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(100).ToList();

            metrics.LoggerCatalog.Add(info);
        }

        metrics.LoggerCatalog = metrics.LoggerCatalog
            .OrderByDescending(l => l.Variables.Count)
            .ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GenerateLoggerCatalogJson(List<LoggerInfo> items, int max)
    {
        var list = (items ?? new List<LoggerInfo>()).Take(max).ToList();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
            var l = list[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(l.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(l.Path ?? string.Empty));
            sb.Append("\",\"store\":\""); sb.Append(EscapeJson(l.Store ?? string.Empty));
            sb.Append("\",\"variables\":[");
            var vars = l.Variables ?? new List<AlarmSourceLinkInfo>();
            for (int v = 0; v < vars.Count; v++)
            {
                if (v > 0) sb.Append(",");
                var item = vars[v];
                sb.Append("{\"field\":\""); sb.Append(EscapeJson(item.FieldName ?? string.Empty));
                sb.Append("\",\"target\":\""); sb.Append(EscapeJson(item.Target ?? string.Empty));
                sb.Append("\",\"resultKind\":\""); sb.Append(EscapeJson(item.ResultKind ?? string.Empty));
                sb.Append("\"}");
            }
            sb.Append("],\"incomingRefs\":[");
            var refs = l.IncomingRefs ?? new List<string>();
            for (int u = 0; u < refs.Count; u++)
            {
                if (u > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(refs[u])); sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    // Security category: Users/Groups/Roles catalogs. Reuses ConverterCatalogInfo's shape;
    // UsageCount/UsagePaths hold incoming cross-references (e.g. a screen bound to a group).
    private void AnalyzeSecurityCategory(ProjectMetrics metrics)
    {
        metrics.SecurityUsers.Clear();
        metrics.SecurityGroups.Clear();
        metrics.SecurityRoles.Clear();

        var securityFolder = FindProjectCategoryFolder("Security");
        if (securityFolder == null)
            return;

        IUANode usersFolder = null, groupsFolder = null, rolesFolder = null;
        foreach (var child in securityFolder.Children)
        {
            if (child == null)
                continue;
            if (string.Equals(child.BrowseName, "Users", StringComparison.Ordinal))
                usersFolder = child;
            else if (string.Equals(child.BrowseName, "Groups", StringComparison.Ordinal))
                groupsFolder = child;
            else if (string.Equals(child.BrowseName, "Roles", StringComparison.Ordinal))
                rolesFolder = child;
        }

        CollectSecurityFolderItems(usersFolder, "User", metrics.SecurityUsers, metrics);
        CollectSecurityFolderItems(groupsFolder, "Group", metrics.SecurityGroups, metrics);
        CollectSecurityFolderItems(rolesFolder, "Role", metrics.SecurityRoles, metrics);
    }

    private void CollectSecurityFolderItems(IUANode folder, string kind, List<ConverterCatalogInfo> target, ProjectMetrics metrics)
    {
        if (folder == null)
            return;

        foreach (var child in folder.Children)
        {
            if (child == null)
                continue;

            var info = new ConverterCatalogInfo
            {
                Name = child.BrowseName,
                TypeName = ResolveNodeTypeName(child),
                Path = SafeNodePath(child),
                Kind = kind
            };

            var key = CanonicalRefKey(info.Path);
            if (!string.IsNullOrEmpty(key)
                && metrics.IncomingRefsByPath.TryGetValue(key, out var incoming) && incoming != null)
            {
                info.UsageCount = incoming.Count;
                info.UsagePaths = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
            }

            target.Add(info);
        }

        target.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    // Reads a LocalizationDictionary's runtime value as a 2D string table
    // (row 0 = locale codes, col 0 = key names).
    private bool TryGetLocalizationTable(IUAVariable variable, out string[,] table)
    {
        table = null;
        try
        {
            var raw = variable?.Value?.Value;
            if (raw is string[,] direct)
            {
                table = direct;
                return true;
            }
            if (raw is Array arr && arr.Rank == 2)
            {
                int rows = arr.GetLength(0);
                int cols = arr.GetLength(1);
                var converted = new string[rows, cols];
                for (int r = 0; r < rows; r++)
                    for (int c = 0; c < cols; c++)
                        converted[r, c] = arr.GetValue(r, c)?.ToString() ?? string.Empty;
                table = converted;
                return true;
            }
        }
        catch
        {
            // Best effort.
        }
        return false;
    }

    // Translations category: LocalizationDictionary tables. Computes per-locale completion
    // (translated/missing key counts, with the actual missing key names) so gaps are directly
    // actionable instead of a bare node count.
    private void AnalyzeTranslationsCategory(ProjectMetrics metrics)
    {
        metrics.TranslationDictionaries.Clear();
        var translationsFolder = FindProjectCategoryFolder("Translations");
        if (translationsFolder == null)
            return;

        foreach (var child in translationsFolder.Children)
        {
            if (!(child is IUAVariable dictVar))
                continue;
            if (!TryGetLocalizationTable(dictVar, out var table))
                continue;

            int rows = table.GetLength(0);
            int cols = table.GetLength(1);
            if (rows < 2 || cols < 2)
                continue;

            var info = new TranslationDictionaryInfo
            {
                Name = child.BrowseName,
                Path = SafeNodePath(child),
                KeyCount = rows - 1
            };

            for (int c = 1; c < cols; c++)
            {
                var locale = table[0, c] ?? string.Empty;
                if (string.IsNullOrWhiteSpace(locale))
                    continue;

                info.Locales.Add(locale);
                var localeStat = new TranslationLocaleStats { Locale = locale };
                for (int r = 1; r < rows; r++)
                {
                    var cell = table[r, c];
                    if (string.IsNullOrWhiteSpace(cell))
                    {
                        localeStat.MissingCount++;
                        var key = table[r, 0] ?? string.Empty;
                        if (localeStat.MissingKeys.Count < 50 && !string.IsNullOrWhiteSpace(key))
                            localeStat.MissingKeys.Add(key);
                    }
                    else
                    {
                        localeStat.TranslatedCount++;
                    }
                }
                info.LocaleStats.Add(localeStat);
            }

            metrics.TranslationDictionaries.Add(info);
        }

        metrics.TranslationDictionaries = metrics.TranslationDictionaries
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private string GenerateTranslationDictionaryJson(List<TranslationDictionaryInfo> items, int max)
    {
        var list = (items ?? new List<TranslationDictionaryInfo>()).Take(max).ToList();
        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < list.Count; i++)
        {
            var d = list[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(d.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(d.Path ?? string.Empty));
            sb.Append("\",\"keyCount\":"); sb.Append(d.KeyCount);
            sb.Append(",\"locales\":[");
            var locales = d.Locales ?? new List<string>();
            for (int l = 0; l < locales.Count; l++)
            {
                if (l > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(locales[l])); sb.Append("\"");
            }
            sb.Append("],\"localeStats\":[");
            var stats = d.LocaleStats ?? new List<TranslationLocaleStats>();
            for (int s = 0; s < stats.Count; s++)
            {
                if (s > 0) sb.Append(",");
                var stat = stats[s];
                sb.Append("{\"locale\":\""); sb.Append(EscapeJson(stat.Locale));
                sb.Append("\",\"translated\":"); sb.Append(stat.TranslatedCount);
                sb.Append(",\"missing\":"); sb.Append(stat.MissingCount);
                sb.Append(",\"missingKeys\":[");
                var missing = stat.MissingKeys ?? new List<string>();
                for (int k = 0; k < missing.Count; k++)
                {
                    if (k > 0) sb.Append(",");
                    sb.Append("\""); sb.Append(EscapeJson(missing[k])); sb.Append("\"");
                }
                sb.Append("]}");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    // Generic catalog for categories with no fixed Types/Instances shape in this project
    // (Reports / Retentivity): lists direct children with their incoming cross-references.
    private void AnalyzeGenericCategoryFolder(string folderBrowseName, List<ConverterCatalogInfo> target, ProjectMetrics metrics)
    {
        target.Clear();
        var folder = FindProjectCategoryFolder(folderBrowseName);
        if (folder == null)
            return;

        foreach (var child in folder.Children)
        {
            if (child == null)
                continue;

            var info = new ConverterCatalogInfo
            {
                Name = child.BrowseName,
                TypeName = ResolveNodeTypeName(child),
                Path = SafeNodePath(child),
                Kind = "Node"
            };

            var key = CanonicalRefKey(info.Path);
            if (!string.IsNullOrEmpty(key)
                && metrics.IncomingRefsByPath.TryGetValue(key, out var incoming) && incoming != null)
            {
                info.UsageCount = incoming.Count;
                info.UsagePaths = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(200).ToList();
            }

            target.Add(info);
        }

        target.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void AnalyzeReportsCategory(ProjectMetrics metrics) => AnalyzeGenericCategoryFolder("Reports", metrics.ReportCatalog, metrics);

    private void AnalyzeRetentivityCategory(ProjectMetrics metrics) => AnalyzeGenericCategoryFolder("Retentivity", metrics.RetentivityCatalog, metrics);

    // MQTT category: Broker (connection/limits) + Client(s), each with their Publisher/Subscriber
    // children. Publisher.DataSource / Subscription.Folder are NodeId pointers resolved the same
    // way as Logger/Recipe "Store" pointers (TryGetNodePointerTargetPath's sibling, ResolveNodeId).
    private void AnalyzeMqttCategory(ProjectMetrics metrics)
    {
        metrics.MqttBroker = null;
        metrics.MqttClients.Clear();

        var mqttFolder = FindProjectCategoryFolder("MQTT");
        if (mqttFolder == null)
            return;

        var descendants = new List<IUANode>();
        CollectDescendants(mqttFolder, descendants, 0);

        var broker = descendants.OfType<MQTTBroker>().FirstOrDefault();
        if (broker != null)
        {
            metrics.MqttBroker = new MqttBrokerInfo
            {
                Name = broker.BrowseName,
                Path = SafeNodePath(broker),
                ListenerPort = broker.ListenerPort,
                MaxConnections = broker.MaxConnections,
                RetainAvailable = broker.RetainAvailable,
                DataPersistence = broker.DataPersistence,
                AnonymousAuthentication = broker.AnonymousAuthentication,
                Running = broker.Running
            };
        }

        foreach (var client in descendants.OfType<MQTTClient>())
        {
            var clientInfo = new MqttClientInfo
            {
                Name = client.BrowseName,
                Path = SafeNodePath(client),
                BrokerAddress = client.BrokerAddress,
                BrokerPort = client.BrokerPort,
                ClientId = client.ClientId,
                SslEnabled = client.SSLTLSEnabled,
                Status = client.Status.ToString()
            };

            var clientPrefix = (clientInfo.Path ?? string.Empty).TrimEnd('/') + "/";
            foreach (var descendant in descendants)
            {
                var path = SafeNodePath(descendant);
                if (string.IsNullOrEmpty(path) || !path.StartsWith(clientPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (descendant is MQTTPublisher publisher)
                {
                    // DataSource is an enum selecting whether Folder or DataLogger holds the
                    // actual NodeId pointer to what's being published.
                    var sourceNodeId = publisher.DataSource == FTOptix.MQTTClient.DataSource.DataLogger
                        ? publisher.DataLogger
                        : publisher.Folder;
                    var dataSourceNode = ResolveNodeId(sourceNodeId);
                    clientInfo.Publishers.Add(new MqttPublisherInfo
                    {
                        Name = publisher.BrowseName,
                        Path = path,
                        Topic = publisher.Topic,
                        QoS = publisher.QoS.ToString(),
                        Retain = publisher.Retain,
                        DataSourcePath = dataSourceNode != null ? SafeNodePath(dataSourceNode) : null,
                        Variables = ListChildNames(dataSourceNode, 50)
                    });
                }
                else if (descendant is MQTTSubscriber subscriber)
                {
                    var subInfo = new MqttSubscriberInfo { Name = subscriber.BrowseName, Path = path };
                    foreach (var subscription in subscriber.Subscriptions)
                    {
                        if (subscription == null)
                            continue;
                        // SubscriptionBase covers both Subscription (has Folder) and
                        // SubscriptionPlainText (no Folder target) variants.
                        var targetNodeId = (subscription as FTOptix.MQTTClient.Subscription)?.Folder;
                        var targetNode = targetNodeId != null ? ResolveNodeId(targetNodeId) : null;
                        subInfo.Subscriptions.Add(new MqttSubscriptionInfo
                        {
                            Topic = subscription.Topic,
                            QoS = subscription.QoS.ToString(),
                            TargetPath = targetNode != null ? SafeNodePath(targetNode) : null,
                            Variables = ListChildNames(targetNode, 50)
                        });
                    }
                    clientInfo.Subscribers.Add(subInfo);
                }
            }

            clientInfo.Publishers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            clientInfo.Subscribers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            metrics.MqttClients.Add(clientInfo);
        }

        metrics.MqttClients.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void CollectDescendants(IUANode root, List<IUANode> result, int depthGuard)
    {
        if (root == null || depthGuard > 60)
            return;

        foreach (var child in root.Children)
        {
            if (child == null)
                continue;
            result.Add(child);
            CollectDescendants(child, result, depthGuard + 1);
        }
    }

    private IUANode ResolveNodeId(NodeId nodeId)
    {
        if (nodeId == null)
            return null;
        try
        {
            return InformationModel.Get(nodeId);
        }
        catch
        {
            return null;
        }
    }

    private List<string> ListChildNames(IUANode node, int max)
    {
        var result = new List<string>();
        if (node == null)
            return result;

        try
        {
            foreach (var child in node.Children)
            {
                if (child == null)
                    continue;
                result.Add(child.BrowseName);
                if (result.Count >= max)
                    break;
            }
        }
        catch
        {
            // Fall through with whatever was collected.
        }

        return result;
    }

    private string GenerateMqttCatalogJson(ProjectMetrics metrics)
    {
        var sb = new StringBuilder();
        sb.Append("{\"broker\":");
        var broker = metrics.MqttBroker;
        if (broker == null)
        {
            sb.Append("null");
        }
        else
        {
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(broker.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(broker.Path ?? string.Empty));
            sb.Append("\",\"listenerPort\":"); sb.Append(broker.ListenerPort);
            sb.Append(",\"maxConnections\":"); sb.Append(broker.MaxConnections);
            sb.Append(",\"retainAvailable\":"); sb.Append(broker.RetainAvailable ? "true" : "false");
            sb.Append(",\"dataPersistence\":"); sb.Append(broker.DataPersistence ? "true" : "false");
            sb.Append(",\"anonymousAuthentication\":"); sb.Append(broker.AnonymousAuthentication ? "true" : "false");
            sb.Append(",\"running\":"); sb.Append(broker.Running ? "true" : "false");
            sb.Append("}");
        }

        sb.Append(",\"clients\":[");
        var clients = metrics.MqttClients ?? new List<MqttClientInfo>();
        for (int i = 0; i < clients.Count; i++)
        {
            var c = clients[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(c.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(c.Path ?? string.Empty));
            sb.Append("\",\"brokerAddress\":\""); sb.Append(EscapeJson(c.BrokerAddress ?? string.Empty));
            sb.Append("\",\"brokerPort\":"); sb.Append(c.BrokerPort);
            sb.Append(",\"clientId\":\""); sb.Append(EscapeJson(c.ClientId ?? string.Empty));
            sb.Append("\",\"sslEnabled\":"); sb.Append(c.SslEnabled ? "true" : "false");
            sb.Append(",\"status\":\""); sb.Append(EscapeJson(c.Status ?? string.Empty));
            sb.Append("\",\"publishers\":[");
            for (int p = 0; p < c.Publishers.Count; p++)
            {
                var pub = c.Publishers[p];
                if (p > 0) sb.Append(",");
                sb.Append("{\"name\":\""); sb.Append(EscapeJson(pub.Name));
                sb.Append("\",\"path\":\""); sb.Append(EscapeJson(pub.Path ?? string.Empty));
                sb.Append("\",\"topic\":\""); sb.Append(EscapeJson(pub.Topic ?? string.Empty));
                sb.Append("\",\"qos\":\""); sb.Append(EscapeJson(pub.QoS ?? string.Empty));
                sb.Append("\",\"retain\":"); sb.Append(pub.Retain ? "true" : "false");
                sb.Append(",\"dataSourcePath\":\""); sb.Append(EscapeJson(pub.DataSourcePath ?? string.Empty));
                sb.Append("\",\"variables\":[");
                for (int v = 0; v < pub.Variables.Count; v++)
                {
                    if (v > 0) sb.Append(",");
                    sb.Append("\""); sb.Append(EscapeJson(pub.Variables[v])); sb.Append("\"");
                }
                sb.Append("]}");
            }
            sb.Append("],\"subscribers\":[");
            for (int s = 0; s < c.Subscribers.Count; s++)
            {
                var sub = c.Subscribers[s];
                if (s > 0) sb.Append(",");
                sb.Append("{\"name\":\""); sb.Append(EscapeJson(sub.Name));
                sb.Append("\",\"path\":\""); sb.Append(EscapeJson(sub.Path ?? string.Empty));
                sb.Append("\",\"subscriptions\":[");
                for (int q = 0; q < sub.Subscriptions.Count; q++)
                {
                    var subscription = sub.Subscriptions[q];
                    if (q > 0) sb.Append(",");
                    sb.Append("{\"topic\":\""); sb.Append(EscapeJson(subscription.Topic ?? string.Empty));
                    sb.Append("\",\"qos\":\""); sb.Append(EscapeJson(subscription.QoS ?? string.Empty));
                    sb.Append("\",\"targetPath\":\""); sb.Append(EscapeJson(subscription.TargetPath ?? string.Empty));
                    sb.Append("\",\"variables\":[");
                    for (int v = 0; v < subscription.Variables.Count; v++)
                    {
                        if (v > 0) sb.Append(",");
                        sb.Append("\""); sb.Append(EscapeJson(subscription.Variables[v])); sb.Append("\"");
                    }
                    sb.Append("]}");
                }
                sb.Append("]}");
            }
            sb.Append("]}");
        }
        sb.Append("]}");
        return sb.ToString();
    }

    // Builds one Model tree node (recursively). Instances of objects and custom
    // datatypes ARE included and expanded to their scalars; only the type
    // DEFINITIONS are excluded (the "Types" folder is skipped by the caller).
    // Returns null for empty containers (objects/folders whose subtree holds no
    // variables).
    private ModelTreeNodeInfo BuildModelTreeNode(IUANode node, ProjectMetrics metrics)
    {
        if (node == null)
            return null;

        var info = new ModelTreeNodeInfo
        {
            Name = node.BrowseName,
            Path = SafeNodePath(node)
        };

        if (node is IUAVariable variable)
            info.DataType = ResolveVariableDataTypeName(variable);

        // Cross-references apply to any node: both scalar variables AND object/instance
        // nodes can be the source or target of a dynamic link (e.g. a UI widget bound to a
        // whole Model object).
        var dynLink = SafeGetDynamicLink(node);
        if (dynLink != null)
        {
            info.LinkTarget = dynLink.Value?.ToString() ?? string.Empty;
            info.LinkKind = ClassifyDynamicLinkResult(node, info.LinkTarget);
            info.IsComplex = ContainsConverter(node);
        }
        if (metrics != null && metrics.IncomingRefsByPath.TryGetValue(CanonicalRefKey(info.Path), out var incoming) && incoming != null)
            info.IncomingRefs = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var child in node.Children)
        {
            var childNode = BuildModelTreeNode(child, metrics);
            if (childNode != null)
                info.Children.Add(childNode);
        }

        bool isVariable = node is IUAVariable;
        info.Kind = (isVariable && info.Children.Count == 0) ? "scalar" : "object";

        // Prune empty containers, unless they are themselves referenced/bound.
        if (!isVariable && info.Children.Count == 0
            && info.IncomingRefs.Count == 0 && string.IsNullOrEmpty(info.LinkTarget))
            return null;

        return info;
    }

    private void WalkModelStats(IUANode node, ProjectMetrics metrics, int depth)
    {
        if (node == null)
            return;

        if (depth > metrics.ModelMaxDepth)
            metrics.ModelMaxDepth = depth;

        if (node is IUAVariable variable)
        {
            var dt = ResolveVariableDataTypeName(variable);
            if (!string.IsNullOrWhiteSpace(dt))
                Increment(metrics.ModelDataTypeDistribution, dt);
        }

        // Instance usage of custom types (objects instantiated from Model types).
        if (node is IUAObject instanceObj && instanceObj.ObjectType != null
            && !IsFrameworkType(instanceObj.ObjectType))
        {
            var typeName = instanceObj.ObjectType.BrowseName;
            if (!string.IsNullOrWhiteSpace(typeName))
                Increment(metrics.ModelTypeUsage, typeName);
        }

        foreach (var child in node.Children)
            WalkModelStats(child, metrics, depth + 1);
    }

    // Project-wide usages of Model custom types: any object instantiated from a Model
    // ObjectType, or any variable declared with a Model struct/enum DataType, ANYWHERE
    // in the project (Model, UI widgets, ...). Records the usage paths per type.
    private void CollectModelTypeUsages(ProjectMetrics metrics)
    {
        if (metrics.ModelTypes.Count == 0)
            return;

        var byName = new Dictionary<string, ModelTypeInfo>(StringComparer.Ordinal);
        foreach (var t in metrics.ModelTypes)
        {
            if (!string.IsNullOrWhiteSpace(t.Name) && !byName.ContainsKey(t.Name))
                byName[t.Name] = t;
        }
        if (byName.Count == 0)
            return;

        var seen = new Dictionary<ModelTypeInfo, HashSet<string>>();

        IEnumerable<IUANode> projectNodes;
        try
        {
            projectNodes = Project.Current.Parent.FindNodesByType<IUANode>();
        }
        catch
        {
            return;
        }

        foreach (var node in projectNodes)
        {
            if (node == null)
                continue;

            string typeName = null;
            if (node is IUAObject obj && obj.ObjectType != null)
                typeName = obj.ObjectType.BrowseName;
            else if (node is IUAVariable variable)
                typeName = ResolveVariableDataTypeName(variable);

            if (string.IsNullOrWhiteSpace(typeName) || !byName.TryGetValue(typeName, out var t))
                continue;

            if (!seen.TryGetValue(t, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seen[t] = set;
            }

            var path = SafeNodePath(node);
            if (set.Add(path))
            {
                t.UsageCount++;
                if (t.UsagePaths.Count < 200)
                    t.UsagePaths.Add(path);
            }
        }

        // Also count "used as kind" references: any node whose NodePointer / dynamic link points at
        // the TYPE node itself (Alias Kind, ListBox item kind, TypeSelector, ...). These were captured
        // in IncomingRefsByPath during the dynamic-link / NodePointer pass, keyed by the type node path.
        foreach (var t in metrics.ModelTypes)
        {
            var typeKey = CanonicalRefKey(t.Path);
            if (string.IsNullOrEmpty(typeKey)
                || !metrics.IncomingRefsByPath.TryGetValue(typeKey, out var kindRefs)
                || kindRefs == null)
                continue;

            if (!seen.TryGetValue(t, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                seen[t] = set;
            }

            foreach (var r in kindRefs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                if (set.Add(r))
                {
                    t.UsageCount++;
                    if (t.UsagePaths.Count < 200)
                        t.UsagePaths.Add(r);
                }
            }
        }
    }

    private void CollectModelTypes(IUANode node, ProjectMetrics metrics)
    {
        if (node == null)
            return;

        var isTypeDef = (node is IUAObjectType || node is IUADataType || node is IUAVariableType)
            && !IsFrameworkType(node);

        if (isTypeDef)
        {
            var info = new ModelTypeInfo
            {
                Name = node.BrowseName,
                Kind = ClassifyModelTypeKind(node),
                SuperType = BuildInheritanceChain(node),
                Path = BuildTypeLocationPath(node)
            };

            foreach (var member in node.Children)
            {
                if (member == null)
                    continue;
                if (!(member is IUAVariable || member is IUAObject))
                    continue;
                info.Fields.Add(new ModelFieldInfo
                {
                    Name = member.BrowseName,
                    Type = ResolveNodeTypeName(member)
                });
            }

            if (string.Equals(info.Kind, "Enumeration", StringComparison.Ordinal))
                metrics.ModelEnumTypeCount++;
            else
                metrics.ModelStructureTypeCount++;

            metrics.ModelTypes.Add(info);
        }

        foreach (var child in node.Children)
            CollectModelTypes(child, metrics);
    }

    private void WalkModelObjectRoot(IUANode node, ModelObjectInfo info, int depth, ProjectMetrics metrics)
    {
        if (node == null)
            return;

        info.NodeCount++;
        if (depth > info.MaxDepth)
            info.MaxDepth = depth;

        var dynLink = SafeGetDynamicLink(node);
        var hasLink = dynLink != null;
        if (hasLink)
            info.DynamicLinkCount++;

        if (node is IUAVariable variable)
        {
            info.VariableCount++;
            var dt = ResolveVariableDataTypeName(variable);
            if (!string.IsNullOrWhiteSpace(dt))
                info.DataTypes.Add(dt);

            var vi = new ModelVariableInfo
            {
                Name = node.BrowseName,
                DataType = dt,
                Path = SafeNodePath(node)
            };
            if (metrics != null && metrics.IncomingRefsByPath.TryGetValue(CanonicalRefKey(vi.Path), out var incoming) && incoming != null)
                vi.IncomingRefs = incoming.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (hasLink)
            {
                vi.LinkTarget = dynLink.Value?.ToString() ?? string.Empty;
                vi.LinkKind = ClassifyDynamicLinkResult(node, vi.LinkTarget);
                vi.IsComplex = ContainsConverter(node);
            }
            info.Variables.Add(vi);
        }
        else if (node is IUAObject)
        {
            info.ObjectCount++;
        }

        foreach (var child in node.Children)
            WalkModelObjectRoot(child, info, depth + 1, metrics);
    }

    private IUAVariable SafeGetDynamicLink(IUANode node)
    {
        try
        {
            return node.Refs.GetVariable(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink);
        }
        catch
        {
            return null;
        }
    }

    private string ClassifyModelTypeKind(IUANode node)
    {
        if (node is IUADataType)
        {
            // Enumerations expose EnumValues / EnumStrings members.
            foreach (var child in node.Children)
            {
                var n = child?.BrowseName ?? string.Empty;
                if (string.Equals(n, "EnumValues", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(n, "EnumStrings", StringComparison.OrdinalIgnoreCase))
                    return "Enumeration";
            }
            return "Data Type";
        }
        if (node is IUAVariableType)
            return "Variable Type";
        return "Object Type";
    }

    private string ResolveNodeTypeName(IUANode node)
    {
        if (node is IUAVariable variable)
        {
            var dt = ResolveVariableDataTypeName(variable);
            if (!string.IsNullOrWhiteSpace(dt))
                return dt;
        }
        if (node is IUAObject obj && obj.ObjectType != null)
            return obj.ObjectType.BrowseName;
        return node?.GetType().Name ?? string.Empty;
    }

    private bool NodeHasDynamicLink(IUANode node)
    {
        return SafeGetDynamicLink(node) != null;
    }

    private void AppendModelTreeNodeJson(StringBuilder sb, ModelTreeNodeInfo node)
    {
        sb.Append("{\"name\":\""); sb.Append(EscapeJson(node.Name ?? string.Empty));
        sb.Append("\",\"path\":\""); sb.Append(EscapeJson(node.Path ?? string.Empty));
        sb.Append("\",\"kind\":\""); sb.Append(EscapeJson(node.Kind ?? "object"));
        sb.Append("\",\"dataType\":\""); sb.Append(EscapeJson(node.DataType ?? string.Empty));
        sb.Append("\",\"linkTarget\":\""); sb.Append(EscapeJson(node.LinkTarget ?? string.Empty));
        sb.Append("\",\"linkKind\":\""); sb.Append(EscapeJson(node.LinkKind ?? "ok"));
        sb.Append("\",\"isComplex\":"); sb.Append(node.IsComplex ? "true" : "false");
        sb.Append(",\"incomingRefs\":[");
        var irefs = node.IncomingRefs ?? new List<string>();
        for (int r = 0; r < irefs.Count; r++)
        {
            if (r > 0) sb.Append(",");
            sb.Append("\""); sb.Append(EscapeJson(irefs[r])); sb.Append("\"");
        }
        sb.Append("],\"children\":[");
        var kids = node.Children ?? new List<ModelTreeNodeInfo>();
        for (int c = 0; c < kids.Count; c++)
        {
            if (c > 0) sb.Append(",");
            AppendModelTreeNodeJson(sb, kids[c]);
        }
        sb.Append("]}");
    }

    private void AppendModelJson(StringBuilder sb, ProjectMetrics metrics)
    {
        // Reuse the accurate project-wide category counts for the headline numbers.
        metrics.CategoryNodeCounts.TryGetValue("model", out var totalNodes);
        metrics.CategoryObjectCounts.TryGetValue("model", out var objectNodes);
        metrics.CategoryVariableCounts.TryGetValue("model", out var variableNodes);
        metrics.CategoryFolderCounts.TryGetValue("model", out var folderNodes);
        metrics.CategoryDynamicLinkCounts.TryGetValue("model", out var dynamicLinks);

        sb.Append(",\"model\":{");
        sb.Append("\"overview\":{");
        sb.Append("\"totalNodes\":"); sb.Append(totalNodes);
        sb.Append(",\"objectNodes\":"); sb.Append(objectNodes);
        sb.Append(",\"variableNodes\":"); sb.Append(variableNodes);
        sb.Append(",\"folderNodes\":"); sb.Append(folderNodes);
        sb.Append(",\"dynamicLinks\":"); sb.Append(dynamicLinks);
        sb.Append(",\"maxDepth\":"); sb.Append(metrics.ModelMaxDepth);
        sb.Append(",\"typeCount\":"); sb.Append(metrics.ModelTypes.Count);
        sb.Append(",\"structureCount\":"); sb.Append(metrics.ModelStructureTypeCount);
        sb.Append(",\"enumCount\":"); sb.Append(metrics.ModelEnumTypeCount);
        sb.Append("}");

        sb.Append(",\"tree\":[");
        for (int i = 0; i < metrics.ModelTree.Count; i++)
        {
            if (i > 0) sb.Append(",");
            AppendModelTreeNodeJson(sb, metrics.ModelTree[i]);
        }
        sb.Append("]");

        sb.Append(",\"dataTypeDistribution\":[");
        var dist = metrics.ModelDataTypeDistribution
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < dist.Count; i++)
        {
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(dist[i].Key));
            sb.Append("\",\"count\":"); sb.Append(dist[i].Value); sb.Append("}");
        }
        sb.Append("]");

        sb.Append(",\"types\":[");
        var types = metrics.ModelTypes
            .OrderByDescending(t => t.Fields.Count)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (int i = 0; i < types.Count; i++)
        {
            var t = types[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(t.Name));
            sb.Append("\",\"kind\":\""); sb.Append(EscapeJson(t.Kind ?? "Type"));
            sb.Append("\",\"superType\":\""); sb.Append(EscapeJson(t.SuperType ?? string.Empty));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(t.Path ?? string.Empty));
            sb.Append("\",\"fieldCount\":"); sb.Append(t.Fields.Count);
            sb.Append(",\"usageCount\":"); sb.Append(t.UsageCount);
            sb.Append(",\"usagePaths\":[");
            for (int u = 0; u < t.UsagePaths.Count; u++)
            {
                if (u > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(t.UsagePaths[u])); sb.Append("\"");
            }
            sb.Append("]");
            sb.Append(",\"fields\":[");
            for (int f = 0; f < t.Fields.Count; f++)
            {
                if (f > 0) sb.Append(",");
                sb.Append("{\"name\":\""); sb.Append(EscapeJson(t.Fields[f].Name));
                sb.Append("\",\"type\":\""); sb.Append(EscapeJson(t.Fields[f].Type)); sb.Append("\"}");
            }
            sb.Append("]}");
        }
        sb.Append("]");

        sb.Append(",\"objects\":[");
        for (int i = 0; i < metrics.ModelObjects.Count; i++)
        {
            var o = metrics.ModelObjects[i];
            if (i > 0) sb.Append(",");
            sb.Append("{\"name\":\""); sb.Append(EscapeJson(o.Name));
            sb.Append("\",\"path\":\""); sb.Append(EscapeJson(o.Path ?? string.Empty));
            sb.Append("\",\"typeName\":\""); sb.Append(EscapeJson(o.TypeName ?? string.Empty));
            sb.Append("\",\"nodeCount\":"); sb.Append(o.NodeCount);
            sb.Append(",\"variableCount\":"); sb.Append(o.VariableCount);
            sb.Append(",\"objectCount\":"); sb.Append(o.ObjectCount);
            sb.Append(",\"maxDepth\":"); sb.Append(o.MaxDepth);
            sb.Append(",\"dynamicLinks\":"); sb.Append(o.DynamicLinkCount);
            sb.Append(",\"dataTypes\":[");
            var dts = o.DataTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            for (int d = 0; d < dts.Count; d++)
            {
                if (d > 0) sb.Append(",");
                sb.Append("\""); sb.Append(EscapeJson(dts[d])); sb.Append("\"");
            }
            sb.Append("]");
            sb.Append(",\"variables\":[");
            for (int v = 0; v < o.Variables.Count; v++)
            {
                var mv = o.Variables[v];
                if (v > 0) sb.Append(",");
                sb.Append("{\"name\":\""); sb.Append(EscapeJson(mv.Name));
                sb.Append("\",\"dataType\":\""); sb.Append(EscapeJson(mv.DataType ?? string.Empty));
                sb.Append("\",\"path\":\""); sb.Append(EscapeJson(mv.Path ?? string.Empty));
                sb.Append("\",\"linkTarget\":\""); sb.Append(EscapeJson(mv.LinkTarget ?? string.Empty));
                sb.Append("\",\"linkKind\":\""); sb.Append(EscapeJson(mv.LinkKind ?? "ok"));
                sb.Append("\",\"isComplex\":"); sb.Append(mv.IsComplex ? "true" : "false");
                sb.Append(",\"incomingRefs\":[");
                var irefs = mv.IncomingRefs ?? new List<string>();
                for (int r = 0; r < irefs.Count; r++)
                {
                    if (r > 0) sb.Append(",");
                    sb.Append("\""); sb.Append(EscapeJson(irefs[r])); sb.Append("\"");
                }
                sb.Append("]");
                sb.Append("}");
            }
            sb.Append("]}");
        }
        sb.Append("]");

        sb.Append("}");
    }

    // Per-category actionable findings, derived from the collected dynamic links.
    // Built with System.Text.Json. The client derives category attention from these
    // (highest finding severity wins). No estimates — only concrete, actionable items.
    private string BuildFindingsJson(ProjectMetrics metrics)
    {
        const int evidenceCap = 25;
        var byCategory = new Dictionary<string, List<object>>();

        foreach (var group in metrics.ProjectDynamicLinkDetails
                     .Where(l => !string.IsNullOrWhiteSpace(l.Category))
                     .GroupBy(l => l.Category))
        {
            var links = group.ToList();
            var findings = new List<object>();

            var broken = links.Where(l => string.Equals(l.ResultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal)).ToList();
            if (broken.Count > 0)
                findings.Add(new
                {
                    severity = "high",
                    kind = "broken-link",
                    title = broken.Count + (broken.Count == 1 ? " broken dynamic link" : " broken dynamic links"),
                    detail = "Target cannot be resolved; raises runtime errors.",
                    evidence = broken.Take(evidenceCap).Select(l => new { path = l.SourcePath, target = l.TargetPath }).ToList()
                });

            if (findings.Count > 0)
                byCategory[group.Key] = findings;
        }

        return JsonSerializer.Serialize(byCategory);
    }

    private void CollectProjectDynamicLinks(ProjectMetrics metrics)
    {
        metrics.TotalDynamicLinks = 0;
        metrics.TotalComplexDynamicLinks = 0;
        metrics.TotalBrokenDynamicLinks = 0;
        metrics.TotalNetLogicNodes = 0;
        metrics.NetLogicDesignNodes = 0;
        metrics.NetLogicRuntimeNodes = 0;
        metrics.CommDriverCount = 0;
        metrics.CommDriverTagCount = 0;
        metrics.OpcUaClientCount = 0;
        metrics.OpcUaServerCount = 0;
        metrics.OpcUaSubscriptionCount = 0;
        metrics.ConverterNodeCount = 0;
        metrics.AlarmNodeCount = 0;
        metrics.RecipeNodeCount = 0;
        metrics.DataStoreNodeCount = 0;
        metrics.LoggerNodeCount = 0;
        metrics.ReportNodeCount = 0;
        metrics.SecurityUserCount = 0;
        metrics.SecurityGroupCount = 0;
        metrics.SecurityRoleCount = 0;
        metrics.TranslationNodeCount = 0;
        metrics.LocaleCount = 0;
        metrics.RetentivityNodeCount = 0;
        metrics.CommDriverBrokenLinkCount = 0;
        metrics.CommDriverUnresolvedLinkCount = 0;
        metrics.CommDriverTagsWithIncomingRef = 0;
        metrics.CommDriverTagsWithoutIncomingRef = 0;
        metrics.CommDriverResolvedIncomingRefCount = 0;
        metrics.CommDriverUnresolvedCandidateCount = 0;
        metrics.CommDriverDriversWithoutTags = 0;
        metrics.OpcUaBrokenLinkCount = 0;
        metrics.OpcUaUnresolvedLinkCount = 0;
        metrics.OpcUaSubscriptionsWithoutClient = 0;
        metrics.GlobalDynamicLinkTargetCounts.Clear();
        metrics.ProjectDynamicLinkDetails.Clear();
        metrics.CommDriverTypeCounts.Clear();
        metrics.OpcUaTypeCounts.Clear();
        metrics.CommDriverTagByDriver.Clear();
        metrics.CommDriverCatalog.Clear();
        metrics.CommDriverOtherObjects.Clear();
        metrics.CommDriverTagUsageCountsByPath.Clear();
        metrics.CommDriverTagIncomingReferencesByPath.Clear();
        metrics.CommDriverUnresolvedCandidates.Clear();
        metrics.IncomingRefsByPath.Clear();
        metrics.ConverterUsagesByPath.Clear();
        metrics.ConverterCatalog.Clear();
        metrics.ConverterOtherItems.Clear();
        metrics.CommDriverTagDetails.Clear();
        metrics.CommDriverTopNodes.Clear();
        metrics.OpcUaTopNodes.Clear();
        metrics.OpcUaEntities.Clear();
        metrics.NetLogicTypeCounts.Clear();
        metrics.NetLogicTopNodes.Clear();
        metrics.NetLogicCatalog.Clear();
        metrics.CategoryNodeCounts.Clear();
        metrics.CategoryDynamicLinkCounts.Clear();
        metrics.CategoryNetLogicCounts.Clear();
        metrics.CategoryFolderCounts.Clear();
        metrics.CategoryObjectCounts.Clear();
        metrics.CategoryVariableCounts.Clear();

        IEnumerable<IUANode> projectNodes;
        try
        {
            projectNodes = Project.Current.Parent.FindNodesByType<IUANode>();
        }
        catch
        {
            projectNodes = Enumerable.Empty<IUANode>();
        }

        // Known real driver names, used to stop ExtractDriverName's folder-name guess from
        // creating catalog entries for non-driver siblings (e.g. NetLogic helper objects placed
        // directly under CommDrivers) that merely share the same first-level folder pattern.
        var knownCommDriverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var driverNode in Project.Current.Parent.FindNodesByType<CommunicationDriver>())
            {
                if (driverNode != null && !string.IsNullOrWhiteSpace(driverNode.BrowseName))
                    knownCommDriverNames.Add(driverNode.BrowseName);
            }
        }
        catch
        {
            // Best effort: if driver discovery fails, only nodes typed as CommunicationDriver
            // (handled inline below) will populate the catalog.
        }

        foreach (var node in projectNodes)
        {
            var typeName = node?.GetType().Name ?? string.Empty;
            var categoryKey = GetProjectCategoryKey(node);
            var safePath = SafeNodePath(node);
            if (!string.IsNullOrWhiteSpace(categoryKey))
            {
                EnsureCategoryCounters(metrics, categoryKey);
                metrics.CategoryNodeCounts[categoryKey]++;
                if (IsFolderLikeNode(node))
                    metrics.CategoryFolderCounts[categoryKey]++;
                if (IsObjectLikeNode(node))
                    metrics.CategoryObjectCounts[categoryKey]++;
                if (IsVariableLikeNode(node))
                    metrics.CategoryVariableCounts[categoryKey]++;
            }

            if (node is CommunicationDriver)
                metrics.CommDriverCount++;
            if (node is FTOptix.CommunicationDriver.Tag)
                metrics.CommDriverTagCount++;
            if (IsOpcUaClientNode(node))
            {
                metrics.OpcUaClientCount++;
                metrics.OpcUaEntities.Add(new OpcUaEntityInsight { Name = node.BrowseName ?? string.Empty, Path = safePath, Kind = "Client" });
            }
            if (IsOpcUaServerNode(node))
            {
                metrics.OpcUaServerCount++;
                metrics.OpcUaEntities.Add(new OpcUaEntityInsight { Name = node.BrowseName ?? string.Empty, Path = safePath, Kind = "Server" });
            }
            if (IsOpcUaSubscriptionNode(node))
            {
                metrics.OpcUaSubscriptionCount++;
                metrics.OpcUaEntities.Add(new OpcUaEntityInsight
                {
                    Name = node.BrowseName ?? string.Empty,
                    Path = safePath,
                    Kind = "Subscription",
                    HasClient = ResolveOpcUaSubscriptionHasClient(node)
                });
            }
            if (typeName.IndexOf("Converter", StringComparison.OrdinalIgnoreCase) >= 0)
                metrics.ConverterNodeCount++;
            if (IsSemanticAlarmNode(node, categoryKey))
                metrics.AlarmNodeCount++;
            if (IsSemanticRecipeNode(node, categoryKey))
                metrics.RecipeNodeCount++;
            if (IsSemanticDataStoreNode(node, categoryKey))
                metrics.DataStoreNodeCount++;
            if (IsSemanticLoggerNode(node, categoryKey))
                metrics.LoggerNodeCount++;
            if (IsSemanticReportNode(node, categoryKey))
                metrics.ReportNodeCount++;
            if (IsSemanticSecurityUserNode(node, categoryKey))
                metrics.SecurityUserCount++;
            if (IsSemanticSecurityGroupNode(node, categoryKey))
                metrics.SecurityGroupCount++;
            if (IsSemanticSecurityRoleNode(node, categoryKey))
                metrics.SecurityRoleCount++;
            if (IsSemanticTranslationNode(node, categoryKey))
                metrics.TranslationNodeCount++;
            if (IsSemanticLocaleNode(node, categoryKey))
                metrics.LocaleCount++;
            if (IsSemanticRetentivityNode(node, categoryKey))
                metrics.RetentivityNodeCount++;

            if (string.Equals(categoryKey, "commdrivers", StringComparison.Ordinal))
            {
                Increment(metrics.CommDriverTypeCounts, typeName);
                var driverName = node is CommunicationDriver
                    ? (node.BrowseName ?? string.Empty)
                    : (ExtractDriverName(safePath) ?? string.Empty);

                // Only create a driver catalog entry for actual driver nodes, or for descendants
                // whose resolved folder name matches a real, previously-detected CommunicationDriver.
                // ExtractDriverName alone is not enough: it returns the first path segment after
                // "CommDrivers" for ANY node (folders, NetLogic helpers, etc.), which would otherwise
                // synthesize a fake driver row for non-driver siblings of the real drivers.
                var isKnownCommDriverName = !string.IsNullOrWhiteSpace(driverName) && knownCommDriverNames.Contains(driverName);
                var driverCatalog = (node is CommunicationDriver || isKnownCommDriverName)
                    ? EnsureCommDriverCatalogEntry(metrics, driverName)
                    : null;

                if (node is CommunicationDriver && !metrics.CommDriverTagByDriver.ContainsKey(driverName))
                    metrics.CommDriverTagByDriver[driverName] = 0;

                // Station/device-like grouping and the "other objects" review list are both derived
                // only from the real project tree -- never inferred from names or assumptions.
                ResolveCommDriverPathInfo(safePath, out var stationName, out var isTopLevelChild, out var isStationNode);
                CommDriverStationInsight station = null;
                if (driverCatalog != null && isKnownCommDriverName && !string.IsNullOrWhiteSpace(stationName))
                {
                  if (isStationNode && IsCommDriverStationNode(node))
                  {
                    station = EnsureCommDriverStation(driverCatalog, stationName);
                    if (station != null)
                      station.Path = safePath;
                  }
                  else if (!isStationNode)
                  {
                    driverCatalog.Stations.TryGetValue(stationName, out station);
                  }
                }
                else if (!(node is CommunicationDriver) && !isKnownCommDriverName && isTopLevelChild)
                {
                    // A direct child of CommDrivers that is neither a real driver nor resolves to
                    // one -- surface it for review instead of silently dropping it from the catalog.
                    EnsureCommDriverOtherObject(metrics, node, safePath, typeName);
                }

                if (node is FTOptix.CommunicationDriver.Tag && node is IUAVariable tagVariable)
                {
                    if (driverCatalog != null)
                        driverCatalog.TagCount++;
                    if (station != null)
                        station.TagCount++;

                    metrics.CommDriverTagDetails.Add(new CommDriverTagInsight
                    {
                        Name = node.BrowseName ?? string.Empty,
                        Path = safePath,
                        DriverName = driverName,
                        StationName = station?.Name,
                        VariableType = typeName,
                        DataType = ResolveVariableDataTypeName(tagVariable),
                        Depth = CalculateDepthFromCategoryRoot(safePath, "CommDrivers")
                    });
                }

                if (node is IUAVariableType variableTypeNode && driverCatalog != null)
                {
                    driverCatalog.VariableTypeNames.Add(variableTypeNode.BrowseName ?? typeName);
                    station?.VariableTypeNames.Add(variableTypeNode.BrowseName ?? typeName);
                }

                if (node is IUADataType dataTypeNode && driverCatalog != null)
                {
                    driverCatalog.DataTypeNames.Add(dataTypeNode.BrowseName ?? typeName);
                    station?.DataTypeNames.Add(dataTypeNode.BrowseName ?? typeName);
                }
            }

            if (string.Equals(categoryKey, "opcua", StringComparison.Ordinal))
                Increment(metrics.OpcUaTypeCounts, typeName);

            var isNetLogic = IsNetLogicNode(node);
            if (isNetLogic)
            {
                metrics.TotalNetLogicNodes++;
                if (!string.IsNullOrWhiteSpace(categoryKey))
                {
                    EnsureCategoryCounters(metrics, categoryKey);
                    metrics.CategoryNetLogicCounts[categoryKey]++;
                }

                if (string.Equals(categoryKey, "netlogic", StringComparison.Ordinal))
                    metrics.NetLogicDesignNodes++;
                else
                    metrics.NetLogicRuntimeNodes++;

                var netLogicTypeName = ResolveNetLogicTypeName(node, metrics, out var netLogicResolutionMethod);
                Increment(metrics.NetLogicTypeCounts, netLogicTypeName);

                var netLogicCatalogEntry = EnsureNetLogicCatalogEntry(metrics, netLogicTypeName, netLogicResolutionMethod);
                if (netLogicCatalogEntry != null)
                {
                    netLogicCatalogEntry.InstanceCount++;
                    netLogicCatalogEntry.Instances.Add(new NetLogicInstanceInfo
                    {
                        Name = node.BrowseName ?? string.Empty,
                        Path = safePath,
                        Category = categoryKey ?? string.Empty
                    });
                }
            }

            IUAVariable dynamicLink;
            try
            {
                dynamicLink = node.Refs.GetVariable(FTOptix.CoreBase.ReferenceTypes.HasDynamicLink);
            }
            catch
            {
                continue;
            }

            var hasDynamicLink = dynamicLink != null;
            var dynamicLinkPath = hasDynamicLink ? (dynamicLink.Value?.ToString() ?? string.Empty) : string.Empty;
            var resultKind = hasDynamicLink ? ClassifyDynamicLinkResult(node, dynamicLinkPath) : DynamicLinkResultKindOk;
            var complex = hasDynamicLink && ContainsConverter(node);

            if (hasDynamicLink)
            {
                if (!string.IsNullOrWhiteSpace(categoryKey))
                {
                    EnsureCategoryCounters(metrics, categoryKey);
                    metrics.CategoryDynamicLinkCounts[categoryKey]++;
                }

                metrics.TotalDynamicLinks++;
                if (complex)
                    metrics.TotalComplexDynamicLinks++;
                if (string.Equals(resultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal))
                    metrics.TotalBrokenDynamicLinks++;

                if (!string.IsNullOrWhiteSpace(dynamicLinkPath))
                    Increment(metrics.GlobalDynamicLinkTargetCounts, dynamicLinkPath);

                metrics.ProjectDynamicLinkDetails.Add(new DynamicLinkInfo
                {
                    ScreenName = TryFindOwningScreenName(node),
                    SourceNode = node.NodeId.ToString(),
                    SourcePath = safePath,
                    TargetPath = dynamicLinkPath,
                    IsComplex = complex,
                    ResultKind = resultKind,
                    Category = categoryKey
                });

                // Incoming cross-references: skip only abstract alias placeholders ({..}); Optix's
                // "used by" wrongly counts those. NodePointer/@NodeId links that target a concrete
                // node (e.g. an MQTT subscriber -> Model/IncomingAlarms@NodeId) ARE real references.
                // Key by a canonical path (anchored at "Objects/", @-suffix stripped) so absolute
                // targets match the model variable even when ResolvePath returns no node at design time.
                if (!PathHasAliasPlaceholder(dynamicLinkPath))
                {
                    var rawKey = CanonicalRefKey(dynamicLinkPath);
                    if (rawKey.IndexOf("Objects/", StringComparison.OrdinalIgnoreCase) >= 0)
                        AddIncomingRef(metrics, rawKey, safePath);

                    // Raw-text check only (no resolution outcome assumed yet) -- used below to decide
                    // whether a failed resolution is worth recording as an unresolved CommDrivers candidate.
                    var targetsCommDriversPath = dynamicLinkPath.IndexOf("CommDrivers", StringComparison.Ordinal) >= 0;
                    var sourcePropertyName = (node is IUAVariable) ? (node.BrowseName ?? string.Empty) : string.Empty;

                    try
                    {
                        var resolveResult = LogicObject?.Context?.ResolvePath(node, dynamicLinkPath);
                        var resolvedTargetNode = TryExtractResolvedNode(resolveResult);
                        if (resolvedTargetNode != null)
                        {
                            AddIncomingRef(metrics, CanonicalRefKey(SafeNodePath(resolvedTargetNode)), safePath);
                            if (resolvedTargetNode is FTOptix.CommunicationDriver.Tag)
                            {
                                var targetCategory = GetProjectCategoryKey(resolvedTargetNode);
                                if (string.Equals(targetCategory, "commdrivers", StringComparison.Ordinal))
                                {
                                    var targetPath = SafeNodePath(resolvedTargetNode);
                                    Increment(metrics.CommDriverTagUsageCountsByPath, targetPath);
                                    metrics.CommDriverResolvedIncomingRefCount++;
                                    AddCommDriverIncomingReference(metrics, targetPath, new CommDriverTagReferenceInsight
                                    {
                                        SourcePath = safePath,
                                        SourceCategory = categoryKey ?? string.Empty,
                                        SourceName = sourcePropertyName,
                                        RawTarget = dynamicLinkPath,
                                        ResolvedTargetPath = targetPath
                                    });
                                }
                            }
                        }
                        else if (targetsCommDriversPath)
                        {
                            // Concrete-looking CommDrivers target that ResolvePath could not resolve.
                            // This is a candidate only -- NOT proof the tag is missing/deleted.
                            AddCommDriverUnresolvedCandidate(metrics, new CommDriverUnresolvedCandidateInsight
                            {
                                SourcePath = safePath,
                                SourceCategory = categoryKey ?? string.Empty,
                                SourceName = sourcePropertyName,
                                RawTarget = dynamicLinkPath,
                                CanonicalTargetPath = rawKey
                            });
                        }
                    }
                    catch
                    {
                        // ResolvePath threw rather than returning null -- still a concrete CommDrivers-
                        // looking target we could not resolve, so record it the same way instead of
                        // silently dropping it.
                        if (targetsCommDriversPath)
                        {
                            AddCommDriverUnresolvedCandidate(metrics, new CommDriverUnresolvedCandidateInsight
                            {
                                SourcePath = safePath,
                                SourceCategory = categoryKey ?? string.Empty,
                                SourceName = sourcePropertyName,
                                RawTarget = dynamicLinkPath,
                                CanonicalTargetPath = rawKey
                            });
                        }
                    }
                }
            }

            // NodePointer references (e.g. MQTT subscriber "Variable", DataLogger targets):
            // NodeId-typed variables whose value points at a project node. These are NOT dynamic
            // links (no HasDynamicLink) but Optix counts them as references, so record them too.
            if (node is IUAVariable pointerVar)
            {
                var pointerTarget = TryGetNodePointerTargetPath(pointerVar);
                if (!string.IsNullOrWhiteSpace(pointerTarget) && !PathHasAliasPlaceholder(pointerTarget))
                {
                    var ptrKey = CanonicalRefKey(pointerTarget);
                    if (ptrKey.IndexOf("Objects/", StringComparison.OrdinalIgnoreCase) >= 0)
                        AddIncomingRef(metrics, ptrKey, safePath);
                }
            }

            // Shared-converter usage: a node whose HasConverter reference points to a reusable
            // converter defined under the Converters folder. Record where each converter is used.
            try
            {
                var convNode = node.Refs.GetNode(FTOptix.CoreBase.ReferenceTypes.HasConverter);
                // A converter instance's ObjectType is the (custom) converter type -> attribute the
                // usage to that TYPE so it matches the Converters catalog.
                if (convNode is IUAObject convObj && convObj.ObjectType != null && !IsFrameworkType(convObj.ObjectType))
                {
                    var convKey = CanonicalRefKey(SafeNodePath(convObj.ObjectType));
                    if (!string.IsNullOrEmpty(convKey))
                        AddConverterUsage(metrics, convKey, safePath);
                }
            }
            catch
            {
                // Converter cross-reference is best effort.
            }

            if (isNetLogic)
                UpdateTopNodeInsights(metrics.NetLogicTopNodes, node, safePath, hasDynamicLink, resultKind);

            if (string.Equals(categoryKey, "commdrivers", StringComparison.Ordinal))
            {
                if (node is FTOptix.CODESYS.Tag)
                {
                    var driverName = ExtractDriverName(safePath);
                    // Only attribute the tag to a driver bucket if the folder name resolves to a
                    // real, known CommunicationDriver -- keeps "Drivers without tags" consistent
                    // with the same driver set used for the catalog above.
                    if (!string.IsNullOrWhiteSpace(driverName) && knownCommDriverNames.Contains(driverName))
                    {
                        if (!metrics.CommDriverTagByDriver.ContainsKey(driverName))
                            metrics.CommDriverTagByDriver[driverName] = 0;
                        metrics.CommDriverTagByDriver[driverName]++;
                    }
                }

                if (hasDynamicLink && string.Equals(resultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal))
                    metrics.CommDriverBrokenLinkCount++;
                if (hasDynamicLink && string.Equals(resultKind, DynamicLinkResultKindUnresolved, StringComparison.Ordinal))
                    metrics.CommDriverUnresolvedLinkCount++;

                UpdateTopNodeInsights(metrics.CommDriverTopNodes, node, safePath, hasDynamicLink, resultKind);
            }

            if (string.Equals(categoryKey, "opcua", StringComparison.Ordinal))
            {
                if (hasDynamicLink && string.Equals(resultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal))
                    metrics.OpcUaBrokenLinkCount++;
                if (hasDynamicLink && string.Equals(resultKind, DynamicLinkResultKindUnresolved, StringComparison.Ordinal))
                    metrics.OpcUaUnresolvedLinkCount++;

                UpdateTopNodeInsights(metrics.OpcUaTopNodes, node, safePath, hasDynamicLink, resultKind);
            }
        }

        metrics.CommDriverDriversWithoutTags = metrics.CommDriverTagByDriver.Values.Count(v => v <= 0);
        metrics.OpcUaSubscriptionsWithoutClient = metrics.OpcUaClientCount == 0 ? metrics.OpcUaSubscriptionCount : 0;

        // Aggregates below are computed over the FULL analyzed CommDriverTagDetails set (never the
        // capped JSON tag-detail sample), so "tags with/without incoming refs" reflects the whole project.
        foreach (var tag in metrics.CommDriverTagDetails)
        {
            if (tag == null || string.IsNullOrWhiteSpace(tag.Path))
                continue;

            if (metrics.CommDriverTagUsageCountsByPath.TryGetValue(tag.Path, out var usageCount))
                tag.UsageCount = usageCount;

            if (metrics.CommDriverTagIncomingReferencesByPath.TryGetValue(tag.Path, out var incomingRefs) && incomingRefs != null)
                tag.IncomingReferences = incomingRefs.OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase).ToList();

            if (tag.UsageCount > 0)
                metrics.CommDriverTagsWithIncomingRef++;
            else
                metrics.CommDriverTagsWithoutIncomingRef++;
        }
    }

    private bool IsRuntimeResolvedDynamicLinkPath(string dynamicLinkPath)
    {
        if (string.IsNullOrEmpty(dynamicLinkPath))
            return false;

        // Alias placeholders, e.g. {AliasName}
        int open = dynamicLinkPath.IndexOf('{');
        if (open >= 0 && dynamicLinkPath.IndexOf('}') > open)
            return true;

        // NodePointer / NodeId bindings defer resolution to runtime
        if (dynamicLinkPath.IndexOf("NodePointer", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        if (dynamicLinkPath.IndexOf("@NodeId", StringComparison.OrdinalIgnoreCase) >= 0)
            return true;

        return false;
    }

    private string TryFindOwningScreenName(IUANode node)
    {
        var current = node;
        int guard = 0;
        while (current != null && guard++ < 128)
        {
            if (IsScreenNode(current))
                return current.BrowseName;
            current = current.Owner;
        }
        return string.Empty;
    }

    private string NormalizeCategoryToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        var sb = new StringBuilder();
        foreach (var ch in token)
        {
            if (char.IsLetterOrDigit(ch))
                sb.Append(char.ToLowerInvariant(ch));
        }

        return sb.ToString();
    }

    private string GetProjectCategoryKey(IUANode node)
    {
        if (node == null)
            return string.Empty;

        var project = Project.Current;
        if (project == null)
            return string.Empty;

        var categoryMap = GetOrBuildProjectCategoryMap();
        var current = node;
        int guard = 0;
        while (current != null && guard++ < 256)
        {
            if (ReferenceEquals(current.Owner, project))
            {
                var nodeId = current.NodeId?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(nodeId) && categoryMap.TryGetValue(nodeId, out var category))
                    return category;
                return string.Empty;
            }

            current = current.Owner;
        }

        return string.Empty;
    }

    private Dictionary<string, string> GetOrBuildProjectCategoryMap()
    {
        if (_projectCategoryByNodeId != null)
            return _projectCategoryByNodeId;

        _projectCategoryByNodeId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rawPairs = new List<KeyValuePair<string, string>>();
        var discoveredKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var child in Project.Current.Children)
            {
                if (!IsFolderLikeNode(child)) continue;

                var key = NormalizeCategoryToken(child.BrowseName);
                var nodeId = child.NodeId?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(nodeId))
                {
                    rawPairs.Add(new KeyValuePair<string, string>(nodeId, key));
                    discoveredKeys.Add(key);
                }
            }
        }
        catch { }

        foreach (var pair in rawPairs)
            _projectCategoryByNodeId[pair.Key] = CanonicalizeCategoryKey(pair.Value, discoveredKeys);

        return _projectCategoryByNodeId;
    }

    private string CanonicalizeCategoryKey(string categoryKey, HashSet<string> knownKeys)
    {
        if (string.IsNullOrWhiteSpace(categoryKey) || knownKeys == null || knownKeys.Count == 0)
            return categoryKey ?? string.Empty;

        // Generic rule: if a top-level category ends with "types" and a base category exists,
        // fold it into that base category.
        if (categoryKey.EndsWith("types", StringComparison.OrdinalIgnoreCase) && categoryKey.Length > 5)
        {
            var stem = categoryKey.Substring(0, categoryKey.Length - 5);
            var candidate = stem.EndsWith("s", StringComparison.OrdinalIgnoreCase) ? stem : (stem + "s");
            if (knownKeys.Contains(candidate))
                return candidate;
        }

        return categoryKey;
    }

    private void EnsureCategoryCounters(ProjectMetrics metrics, string categoryKey)
    {
        if (string.IsNullOrWhiteSpace(categoryKey))
            return;

        if (!metrics.CategoryNodeCounts.ContainsKey(categoryKey))
            metrics.CategoryNodeCounts[categoryKey] = 0;
        if (!metrics.CategoryDynamicLinkCounts.ContainsKey(categoryKey))
            metrics.CategoryDynamicLinkCounts[categoryKey] = 0;
        if (!metrics.CategoryNetLogicCounts.ContainsKey(categoryKey))
            metrics.CategoryNetLogicCounts[categoryKey] = 0;
        if (!metrics.CategoryFolderCounts.ContainsKey(categoryKey))
            metrics.CategoryFolderCounts[categoryKey] = 0;
        if (!metrics.CategoryObjectCounts.ContainsKey(categoryKey))
            metrics.CategoryObjectCounts[categoryKey] = 0;
        if (!metrics.CategoryVariableCounts.ContainsKey(categoryKey))
            metrics.CategoryVariableCounts[categoryKey] = 0;
    }

    private List<string> GetOrderedCategoryKeys(ProjectMetrics metrics)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in metrics.CategoryNodeCounts.Keys)
            keys.Add(key);
        foreach (var key in metrics.CategoryDynamicLinkCounts.Keys)
            keys.Add(key);
        foreach (var key in metrics.CategoryNetLogicCounts.Keys)
            keys.Add(key);
        foreach (var key in metrics.CategoryFolderCounts.Keys)
            keys.Add(key);
        foreach (var key in metrics.CategoryObjectCounts.Keys)
            keys.Add(key);
        foreach (var key in metrics.CategoryVariableCounts.Keys)
            keys.Add(key);

        return keys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private bool IsNetLogicNode(IUANode node)
    {
        if (node == null)
            return false;

        if (node is BaseNetLogic)
            return true;

        var typeName = node.GetType().Name;
        return typeName.IndexOf("NetLogic", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsOpcUaClientNode(IUANode node)
    {
        var typeName = node?.GetType().Name ?? string.Empty;
        return typeName.IndexOf("OPCUAClient", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsOpcUaServerNode(IUANode node)
    {
        var typeName = node?.GetType().Name ?? string.Empty;
        return typeName.IndexOf("OPCUAServer", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsOpcUaSubscriptionNode(IUANode node)
    {
        var typeName = node?.GetType().Name ?? string.Empty;
        return typeName.IndexOf("Subscription", StringComparison.OrdinalIgnoreCase) >= 0
            && typeName.IndexOf("OPCUA", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Walks the owner chain up from a Subscription node looking for its owning OPC-UA Client
    // (Subscriptions are structurally nested under their Client in the FT Optix object model).
    // Returns false when the chain is exhausted with no Client found (resolved: none), or
    // null when the walk cannot be trusted (guard limit hit / unexpected error) rather than guessing.
    private bool? ResolveOpcUaSubscriptionHasClient(IUANode node)
    {
        try
        {
            var current = node?.Owner;
            int guard = 0;
            while (current != null && guard++ < 128)
            {
                if (IsOpcUaClientNode(current))
                    return true;
                current = current.Owner;
            }
            return guard >= 128 ? (bool?)null : false;
        }
        catch
        {
            return null;
        }
    }

    private bool HasBrowseName(IUANode node, string token)
    {
        return (node?.BrowseName ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool HasPathToken(IUANode node, string token)
    {
        return SafeNodePath(node).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsSemanticAlarmNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "alarms", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Alarm") || HasTypeName(node, "Condition") || HasBrowseName(node, "Alarm");
    }

    private bool IsSemanticRecipeNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "recipes", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Recipe") || HasTypeName(node, "Ingredient") || HasBrowseName(node, "Recipe");
    }

    private bool IsSemanticDataStoreNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "datastores", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Store") || HasTypeName(node, "Database") || HasTypeName(node, "Table")
            || HasBrowseName(node, "Store") || HasBrowseName(node, "Table");
    }

    private bool IsSemanticLoggerNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "loggers", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Logger") || HasTypeName(node, "DataLogger") || HasTypeName(node, "Historian")
            || HasBrowseName(node, "Logger");
    }

    private bool IsSemanticReportNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "reports", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Report") || HasTypeName(node, "Template") || HasTypeName(node, "ReportViewer")
            || HasBrowseName(node, "Report");
    }

    private bool IsSemanticSecurityUserNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "security", StringComparison.Ordinal) && !HasPathToken(node, "/Security/"))
            return false;
        return HasTypeName(node, "User") || HasBrowseName(node, "User");
    }

    private bool IsSemanticSecurityGroupNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "security", StringComparison.Ordinal) && !HasPathToken(node, "/Security/"))
            return false;
        return HasTypeName(node, "Group") || HasBrowseName(node, "Group");
    }

    private bool IsSemanticSecurityRoleNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "security", StringComparison.Ordinal) && !HasPathToken(node, "/Security/"))
            return false;
        return HasTypeName(node, "Role") || HasBrowseName(node, "Role");
    }

    private bool IsSemanticTranslationNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "translations", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Translation") || HasTypeName(node, "Dictionary") || HasBrowseName(node, "Translation");
    }

    private bool IsSemanticLocaleNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "translations", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Locale") || HasBrowseName(node, "Locale") || HasBrowseName(node, "Language");
    }

    private bool IsSemanticRetentivityNode(IUANode node, string categoryKey)
    {
        if (!string.Equals(categoryKey, "retentivity", StringComparison.Ordinal))
            return false;
        return HasTypeName(node, "Retent") || HasTypeName(node, "Persistent") || HasBrowseName(node, "Retent") || HasBrowseName(node, "Persist");
    }

    private void UpdateTopNodeInsights(List<CategoryNodeInsight> list, IUANode node, string path, bool hasDynamicLink, string resultKind)
    {
        if (node == null || list == null)
            return;

        int fanOut = 0;
        try
        {
            fanOut = node.Children.Count();
        }
        catch
        {
            fanOut = 0;
        }

        var score = fanOut;
        if (hasDynamicLink)
            score += 3;
        if (string.Equals(resultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal))
            score += 5;
        else if (string.Equals(resultKind, DynamicLinkResultKindUnresolved, StringComparison.Ordinal))
            score += 3;

        list.Add(new CategoryNodeInsight
        {
            Name = node.BrowseName ?? string.Empty,
            Path = path,
            TypeName = node.GetType().Name,
            FanOut = fanOut,
            HasDynamicLink = hasDynamicLink,
            ResultKind = resultKind,
            Score = score
        });
    }

    private bool HasTypeName(IUANode node, string token)
    {
        return (node?.GetType().Name ?? string.Empty).IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private bool IsFolderLikeNode(IUANode node)
    {
        return HasTypeName(node, "Folder");
    }

    private bool IsObjectLikeNode(IUANode node)
    {
        return node is IUAObject;
    }

    private bool IsVariableLikeNode(IUANode node)
    {
        return node is IUAVariable;
    }

    private bool ContainsConverter(IUANode node)
    {
        if (node is Converter)
            return true;

        foreach (var child in node.Children)
        {
            if (ContainsConverter(child))
                return true;
        }

        return false;
    }

    private void BuildTypeUsageMap(ProjectMetrics metrics)
    {
        metrics.TypeUsageMap.Clear();

        foreach (var typeInfo in metrics.TypeCatalog.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            metrics.TypeUsageMap[typeInfo.Name] = typeInfo.UsagePaths
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private void BuildTypeCatalogDetails(ProjectMetrics metrics)
    {
        foreach (var typeInfo in metrics.TypeCatalog.Values)
            AnalyzeTypeContent(typeInfo);
    }

    private void AnalyzeTypeContent(TypeMapInfo typeInfo)
    {
        if (typeInfo == null || typeInfo.IsAnalyzed)
            return;

        typeInfo.IsAnalyzed = true;

        if (typeInfo.TypeNode == null)
            return;

        AnalyzeTypeContentRecursive(typeInfo.TypeNode, typeInfo, new HashSet<string>());

        if (typeInfo.TypeNode is IUAObjectType objectType)
        {
            var parent = objectType.SuperType;
            int guard = 0;
            while (parent != null && guard++ < 64)
            {
                var parentName = parent.BrowseName;
                if (string.Equals(parentName, "Screen", StringComparison.OrdinalIgnoreCase))
                    break;

                if (IsCustomUIType(parent) && !string.Equals(parentName, typeInfo.Name, StringComparison.OrdinalIgnoreCase))
                    typeInfo.InternalDependencies.Add(parentName);

                parent = parent.SuperType;
            }
        }
    }

    private void AnalyzeTypeContentRecursive(IUANode node, TypeMapInfo typeInfo, HashSet<string> visitedNodeIds)
    {
        if (node == null || typeInfo == null)
            return;

        var nodeKey = node.NodeId.ToString();
        if (!visitedNodeIds.Add(nodeKey))
            return;

        typeInfo.ContentNodeCount++;

        if (node is IUAVariable variable)
        {
            typeInfo.ContentVariableCount++;
            if (HasDynamicLink(variable))
                typeInfo.ContentDynamicLinkCount++;

            var dataTypeNode = TryGetCustomDataTypeNode(variable);
            if (dataTypeNode != null && !string.Equals(dataTypeNode.BrowseName, typeInfo.Name, StringComparison.OrdinalIgnoreCase))
                typeInfo.InternalDependencies.Add(dataTypeNode.BrowseName);
        }

        var childType = TryGetCustomUIType(node);
        if (childType != null && !string.Equals(childType.BrowseName, typeInfo.Name, StringComparison.OrdinalIgnoreCase))
            typeInfo.InternalDependencies.Add(childType.BrowseName);

        foreach (var child in node.Children)
            AnalyzeTypeContentRecursive(child, typeInfo, visitedNodeIds);
    }

    private void BuildInterdependencyMap(ProjectMetrics metrics)
    {
        foreach (var screen in metrics.Screens.Values)
        {
            foreach (var dep in screen.Dependencies)
            {
                if (!metrics.InterdependencyMap.ContainsKey(screen.Name))
                    metrics.InterdependencyMap[screen.Name] = new HashSet<string>();
                metrics.InterdependencyMap[screen.Name].Add(dep);
            }
        }
    }

    private void TraverseDrivers(IUANode driversRoot, ProjectMetrics metrics, string currentPath)
    {
        if (driversRoot == null)
            return;

        if (driversRoot is CommunicationDriver)
        {
            var driverName = driversRoot.BrowseName;
            if (!metrics.Drivers.ContainsKey(driverName))
                metrics.Drivers[driverName] = new DriverInfo { Name = driverName, Type = driversRoot.GetType().Name };
        }

        var currentDriverName = ExtractDriverName(currentPath);
        if (!string.IsNullOrEmpty(currentDriverName) && metrics.Drivers.ContainsKey(currentDriverName))
        {
            var driver = metrics.Drivers[currentDriverName];
            driver.NodeCount++;

            if (driversRoot is FTOptix.CommunicationDriver.Tag)
            {
                driver.TagCount++;
                driver.TagNames.Add(driversRoot.BrowseName);
            }
        }

        foreach (var child in driversRoot.Children)
        {
            var childPath = string.Concat(currentPath, "/", child.BrowseName);
            TraverseDrivers(child, metrics, childPath);
        }
    }

    private string ExtractDriverName(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.Contains("CommDrivers"))
            return null;

        var parts = path.Split('/');
        var idx = Array.IndexOf(parts, "CommDrivers");
        if (idx >= 0 && idx + 1 < parts.Length)
            return parts[idx + 1];

        return null;
    }

    // Resolves driver/station-level path info for a CommDrivers node in one place. stationName is
    // the first-level child folder/object name directly under the driver (e.g. "CLX1" under
    // "Ethernet_IP"), derived purely from the project tree -- no semantic meaning is inferred.
    private void ResolveCommDriverPathInfo(string path, out string stationName, out bool isTopLevelChild, out bool isStationNode)
    {
        stationName = null;
        isTopLevelChild = false;
        isStationNode = false;

        if (string.IsNullOrEmpty(path) || !path.Contains("CommDrivers"))
            return;

        var parts = path.Split('/');
        var idx = Array.IndexOf(parts, "CommDrivers");
        if (idx < 0)
            return;

        isTopLevelChild = parts.Length == idx + 2;

        if (idx + 2 < parts.Length)
            stationName = parts[idx + 2];

        isStationNode = parts.Length == idx + 3;
    }

      private bool IsCommDriverStationNode(IUANode node)
      {
        if (node is not IUAObject objectNode || node is IUAVariable)
          return false;

        for (var objectType = objectNode.ObjectType; objectType != null; objectType = objectType.SuperType)
        {
          if ((objectType.BrowseName ?? string.Empty).EndsWith("Station", StringComparison.OrdinalIgnoreCase))
            return true;
        }

        return false;
      }

    private void Increment(Dictionary<string, int> dict, string key)
    {
        if (string.IsNullOrEmpty(key))
            key = "<empty>";

        if (!dict.ContainsKey(key))
            dict[key] = 0;
        dict[key]++;
    }

    private int GetInheritanceDepth(Type type)
    {
        int depth = 0;
        var current = type?.BaseType;
        while (current != null && current != typeof(object))
        {
            depth++;
            current = current.BaseType;
        }
        return depth;
    }

    private string SafeBaseType(Type type)
    {
        return type?.BaseType?.Name ?? "Unknown";
    }

    private string SafeNodePath(IUANode node)
    {
        try
        {
            var segments = new Stack<string>();
            var current = node;
            while (current != null)
            {
                segments.Push(current.BrowseName);
                current = current.Owner;
            }
            return string.Join("/", segments);
        }
        catch
        {
            return node?.BrowseName ?? "<unknown>";
        }
    }

    // Resolves an alarm "Message" variable value (typically a LocalizedText) to a readable
    // display string instead of the raw struct dump (e.g. "NamespaceIndex: 100, TextId: ...").
    // Prefers the resolved Text if populated, falls back to TextId (the design-time resource
    // key), and only falls back to the raw ToString() if neither can be read.
    private string FormatLocalizedTextValue(object value)
    {
        if (value == null)
            return string.Empty;

        try
        {
            var type = value.GetType();

            var text = type.GetProperty("Text")?.GetValue(value) as string;
            if (!string.IsNullOrEmpty(text))
                return text;

            var textId = type.GetProperty("TextId")?.GetValue(value) as string;
            if (!string.IsNullOrEmpty(textId))
                return textId;
        }
        catch
        {
            // Fall through to the raw representation below.
        }

        return value.ToString() ?? string.Empty;
    }

    // Location of the type node relative to the UI category, e.g.
    // "UI/Screens/OvensOverview", so the user knows where to find it.
    private string BuildTypeLocationPath(IUANode node)
    {
        if (node == null)
            return string.Empty;
        var path = SafeNodePath(node);
        var uiIdx = path.IndexOf("UI/", StringComparison.Ordinal);
        if (uiIdx > 0)
            path = path.Substring(uiIdx);
        return path;
    }

    // Determines whether a type node belongs to the framework (FTOptix / OPC base
    // types) rather than being a project-defined custom type.
    private bool IsFrameworkType(IUANode node)
    {
        var ns = TryGetNamespaceUri(node) ?? string.Empty;
        if (ns.Length == 0)
            return true; // standard/OPC base types report no project namespace
        return ns.IndexOf("FTOptix", StringComparison.OrdinalIgnoreCase) >= 0
            || ns.IndexOf("opcfoundation", StringComparison.OrdinalIgnoreCase) >= 0
            || ns.IndexOf("http://opc", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // Human-readable supertype chain of a type, walking the Optix TYPE MODEL
    // (IUAObjectType.SuperType) rather than the .NET class hierarchy. The .NET
    // type of a custom type node resolves to the generic meta class
    // (ScreenType/PanelType/...), which is not what the user wants; the model
    // supertypes carry the real names. Example for OvensOverview (Supertype
    // BaseScreen, itself a Screen): "Screen \u2192 BaseScreen". Custom supertypes are
    // listed up to and including the first framework base type; the type's own
    // name is omitted because it is already shown as the card title.
    private string BuildInheritanceChain(IUANode node)
    {
        if (node == null)
            return string.Empty;

        const string arrow = " \u2192 ";

        var typeNode = node as IUAObjectType;
        if (typeNode == null && node is IUAObject obj)
            typeNode = obj.ObjectType;
        if (typeNode == null)
            return node.BrowseName ?? string.Empty;

        var chain = new List<string>();
        var current = typeNode;
        int guard = 0;
        while (current != null && guard++ < 64)
        {
            if (!string.IsNullOrWhiteSpace(current.BrowseName))
                chain.Add(current.BrowseName);
            if (IsFrameworkType(current))
                break; // include the first framework base, then stop
            current = current.SuperType;
        }

        // Drop the type's own name (already shown as the card title) when ancestry exists.
        if (chain.Count > 1)
            chain.RemoveAt(0);

        chain.Reverse(); // base-first, e.g. "Screen -> BaseScreen"
        return string.Join(arrow, chain);
    }

    private string EscapeHtml(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&#39;");
    }

    private string EscapeJson(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "\\r")
            .Replace("\n", "\\n")
            .Replace("</", "<\\/");
    }

    private string MakeAnchorId(string prefix, string raw)
    {
        var sb = new StringBuilder();
        sb.Append(prefix);
        sb.Append("-");

        bool lastWasDash = true;
        foreach (var ch in raw ?? string.Empty)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        if (sb.Length > 0 && sb[sb.Length - 1] == '-')
            sb.Length--;

        return sb.Length == 0 ? prefix : sb.ToString();
    }

    private string GenerateTypeDistributionJson(ProjectMetrics metrics)
    {
        var items = metrics.UITypeCounts
            .OrderByDescending(kv => kv.Value)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (i > 0)
                sb.Append(",");

            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(item.Key));
            sb.Append("\",\"count\":");
            sb.Append(item.Value);
            sb.Append(",\"percentage\":");
            sb.Append((item.Value * 100.0 / Math.Max(1, metrics.UITotalNodes)).ToString("0.0", CultureInfo.InvariantCulture));
            sb.Append("}");
        }

        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateCountDistributionJson(Dictionary<string, int> counts, int maxItems)
    {
        var items = (counts ?? new Dictionary<string, int>())
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(",");
            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(items[i].Key));
            sb.Append("\",\"count\":");
            sb.Append(items[i].Value);
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateNodeInsightsJson(List<CategoryNodeInsight> nodes, int maxItems)
    {
        var items = (nodes ?? new List<CategoryNodeInsight>())
            .OrderByDescending(n => n.Score)
            .ThenByDescending(n => n.FanOut)
            .ThenBy(n => n.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
                sb.Append(",");
            var node = items[i];
            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(node.Name));
            sb.Append("\",\"path\":\"");
            sb.Append(EscapeJson(node.Path));
            sb.Append("\",\"type\":\"");
            sb.Append(EscapeJson(node.TypeName));
            sb.Append("\",\"fanOut\":");
            sb.Append(node.FanOut);
            sb.Append(",\"hasDynamicLink\":");
            sb.Append(node.HasDynamicLink ? "true" : "false");
            sb.Append(",\"resultKind\":\"");
            sb.Append(EscapeJson(node.ResultKind ?? DynamicLinkResultKindOk));
            sb.Append("\"}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GetNetSolutionRootPath()
    {
        return Path.Combine(Project.Current.ProjectDirectory, "NetSolution");
    }

    private void AnalyzeSolutionCode(ProjectMetrics metrics)
    {
        metrics.SolutionCsFileCount = 0;
        metrics.SolutionNetLogicFileCount = 0;
        metrics.SolutionCodeLineCount = 0;
        metrics.SolutionMethodCount = 0;
        metrics.SolutionComplexityScore = 0;
        metrics.SolutionCodeFiles.Clear();
        metrics.SolutionTopFunctionCalls.Clear();

        var netSolutionPath = GetNetSolutionRootPath();
        if (!Directory.Exists(netSolutionPath))
            return;

        var files = Directory
            .GetFiles(netSolutionPath, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0
                && path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0)
            .ToList();

        var globalCallCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            CodeFileInsight insight;
            try
            {
                insight = AnalyzeCodeFile(file, netSolutionPath, globalCallCounts);
            }
            catch
            {
                continue;
            }

            metrics.SolutionCsFileCount++;
            metrics.SolutionCodeLineCount += insight.CodeLines;
            metrics.SolutionMethodCount += insight.MethodCount;
            metrics.SolutionComplexityScore += insight.CyclomaticComplexity;
            if (insight.IsNetLogicFile)
                metrics.SolutionNetLogicFileCount++;

            metrics.SolutionCodeFiles.Add(insight);
        }

        metrics.SolutionCodeFiles = metrics.SolutionCodeFiles
            .OrderByDescending(f => f.ImpactScore)
            .ThenByDescending(f => f.CyclomaticComplexity)
            .ThenByDescending(f => f.CodeLines)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(120)
            .ToList();

        metrics.SolutionTopFunctionCalls = globalCallCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .Select(kv => new CodeFunctionUsage { Name = kv.Key, Count = kv.Value })
            .ToList();
    }

    private CodeFileInsight AnalyzeCodeFile(string filePath, string rootPath, Dictionary<string, int> globalCallCounts)
    {
        var text = File.ReadAllText(filePath);
        var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

        var nonEmptyLines = lines.Count(line => !string.IsNullOrWhiteSpace(line));
        var codeLines = lines.Count(line =>
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                return false;
            if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal) || trimmed.StartsWith("*/", StringComparison.Ordinal))
                return false;
            return true;
        });

        var classCount = Regex.Matches(text, @"\b(class|struct|record)\s+[A-Za-z_][A-Za-z0-9_]*").Count;
        var methodCount = Regex.Matches(text, @"\b(public|private|protected|internal)\s+(static\s+)?(async\s+)?[A-Za-z_][A-Za-z0-9_<>,\[\]\.?\s]*\s+[A-Za-z_][A-Za-z0-9_]*\s*\(").Count;
        var cyclomatic = 1
            + Regex.Matches(text, @"\bif\b").Count
            + Regex.Matches(text, @"\bfor\b").Count
            + Regex.Matches(text, @"\bforeach\b").Count
            + Regex.Matches(text, @"\bwhile\b").Count
            + Regex.Matches(text, @"\bcase\b").Count
            + Regex.Matches(text, @"\bcatch\b").Count
            + Regex.Matches(text, @"&&").Count
            + Regex.Matches(text, @"\|\|").Count;

        var callCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var matches = Regex.Matches(text, @"\b([A-Za-z_][A-Za-z0-9_]*)\s*\(");
        foreach (Match match in matches)
        {
            if (!match.Success || match.Groups.Count < 2)
                continue;

            var callName = match.Groups[1].Value;
            if (ShouldIgnoreCallName(callName))
                continue;

            Increment(callCounts, callName);
            Increment(globalCallCounts, callName);
        }

        var dominantCalls = callCounts
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .Select(kv => kv.Key + "(" + kv.Value.ToString(CultureInfo.InvariantCulture) + ")")
            .ToList();

        var isNetLogicFile = text.IndexOf("BaseNetLogic", StringComparison.Ordinal) >= 0
            || text.IndexOf("FTOptix.NetLogic", StringComparison.Ordinal) >= 0;

        var impactScore = (int)Math.Round(
            cyclomatic * 2.0
            + methodCount * 1.6
            + codeLines / 20.0
            + classCount * 1.5
            + (isNetLogicFile ? 10.0 : 0.0), MidpointRounding.AwayFromZero);

        if (codeLines > 900)
            impactScore += 8;
        else if (codeLines > 500)
            impactScore += 4;

        var relativePath = MakeRelativePath(rootPath, filePath);
        return new CodeFileInsight
        {
            Path = relativePath,
            TotalLines = nonEmptyLines,
            CodeLines = codeLines,
            ClassCount = classCount,
            MethodCount = methodCount,
            CyclomaticComplexity = cyclomatic,
            ImpactScore = impactScore,
            IsNetLogicFile = isNetLogicFile,
            DominantCalls = dominantCalls
        };
    }

    private bool ShouldIgnoreCallName(string callName)
    {
        if (string.IsNullOrWhiteSpace(callName))
            return true;

        switch (callName)
        {
            case "if":
            case "for":
            case "foreach":
            case "while":
            case "switch":
            case "catch":
            case "return":
            case "nameof":
            case "typeof":
            case "default":
            case "base":
            case "this":
            case "new":
            case "set":
            case "get":
                return true;
            default:
                return false;
        }
    }

    private string MakeRelativePath(string rootPath, string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootPath) || string.IsNullOrWhiteSpace(fullPath))
                return fullPath ?? string.Empty;

            var relative = Path.GetRelativePath(rootPath, fullPath);
            return relative.Replace('\\', '/');
        }
        catch
        {
            return fullPath ?? string.Empty;
        }
    }

    // CLR type names FT Optix uses as a generic runtime host for attached-script NetLogic -
    // these carry no customer-facing meaning and must never be reported as the "type".
    private static readonly HashSet<string> GenericNetLogicWrapperTypeNames =
        new HashSet<string>(StringComparer.Ordinal) { "NetLogicObject" };

    // Populates metrics.NetLogicClassNames from NetSolution *.cs sources so attached-script
    // NetLogic (whose CLR type is only the generic "NetLogicObject" wrapper) can still be
    // resolved to its real class by matching the instance BrowseName.
    private void ScanNetLogicClassNames(ProjectMetrics metrics)
    {
        metrics.NetLogicClassNames.Clear();

        var netSolutionPath = GetNetSolutionRootPath();
        if (!Directory.Exists(netSolutionPath))
            return;

        IEnumerable<string> files;
        try
        {
            files = Directory
                .GetFiles(netSolutionPath, "*.cs", SearchOption.AllDirectories)
                .Where(path => path.IndexOf("\\bin\\", StringComparison.OrdinalIgnoreCase) < 0
                    && path.IndexOf("\\obj\\", StringComparison.OrdinalIgnoreCase) < 0);
        }
        catch
        {
            return;
        }

        foreach (var file in files)
        {
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch
            {
                continue;
            }

            // Only classes directly derived from BaseNetLogic are genuine NetLogic type
            // declarations - the only reliable, evidence-based signal available for
            // attached-script NetLogic (see ResolveNetLogicTypeName).
            var matches = Regex.Matches(text, @"\bclass\s+([A-Za-z_][A-Za-z0-9_]*)\s*:\s*BaseNetLogic\b");
            foreach (Match match in matches)
            {
                var className = match.Groups[1].Value;
                if (!metrics.NetLogicClassNames.ContainsKey(className))
                    metrics.NetLogicClassNames[className] = MakeRelativePath(netSolutionPath, file);
            }
        }
    }

    private string ResolveNetLogicTypeName(IUANode node, ProjectMetrics metrics, out string resolutionMethod)
    {
        resolutionMethod = "Unknown";
        if (node == null)
            return "<unknown>";

        // Tier 1 - custom NetLogic ObjectType: the CLR type is already a distinct, meaningful
        // class (not the generic wrapper), so it IS the customer-facing type.
        var clrTypeName = node.GetType()?.Name;
        if (!string.IsNullOrWhiteSpace(clrTypeName) && !GenericNetLogicWrapperTypeNames.Contains(clrTypeName))
        {
            resolutionMethod = "TypeDefinition";
            return clrTypeName;
        }

        // Tier 2 - attached-script NetLogic: the CLR type is only the generic wrapper
        // (e.g. "NetLogicObject"). On-disk YAML also has no more specific type reference for
        // these nodes (verified: they all serialize as "Type: NetLogic"), so the only
        // remaining evidence is the instance BrowseName, matched against a class actually
        // declared as ": BaseNetLogic" under NetSolution.
        var browseName = node.BrowseName;
        if (!string.IsNullOrWhiteSpace(browseName)
            && metrics?.NetLogicClassNames != null
            && metrics.NetLogicClassNames.ContainsKey(browseName))
        {
            resolutionMethod = "ClassNameMatch";
            return browseName;
        }

        // Tier 3 - no class evidence available (renamed instance, source not found, etc.):
        // fall back to BrowseName. Still never the generic wrapper name.
        if (!string.IsNullOrWhiteSpace(browseName))
        {
            resolutionMethod = "BrowseNameFallback";
            return browseName;
        }

        return "<unknown>";
    }

    private NetLogicTypeCatalogInsight EnsureNetLogicCatalogEntry(ProjectMetrics metrics, string typeName, string resolutionMethod)
    {
        if (metrics == null)
            return null;

        var key = string.IsNullOrWhiteSpace(typeName) ? "<unknown>" : typeName;
        if (!metrics.NetLogicCatalog.TryGetValue(key, out var entry))
        {
            entry = new NetLogicTypeCatalogInsight { Name = key, ResolutionMethod = string.IsNullOrWhiteSpace(resolutionMethod) ? "Unknown" : resolutionMethod };
            if (metrics.NetLogicClassNames != null && metrics.NetLogicClassNames.TryGetValue(key, out var sourceFile))
                entry.SourceFile = sourceFile;
            metrics.NetLogicCatalog[key] = entry;
        }

        return entry;
    }

    private string ResolveVariableDataTypeName(IUAVariable variable)
    {
        try
        {
            if (variable?.DataType == null)
                return string.Empty;

            IUANode dataTypeNode;
            try
            {
                dataTypeNode = InformationModel.Get(variable.DataType);
            }
            catch
            {
                dataTypeNode = null;
            }

            if (dataTypeNode != null && !string.IsNullOrWhiteSpace(dataTypeNode.BrowseName))
                return dataTypeNode.BrowseName;

            return variable.DataType.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }

    private int CalculateDepthFromCategoryRoot(string path, string categoryRootName)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(categoryRootName))
            return 0;

        var parts = path.Split('/');
        var idx = Array.FindIndex(parts, p => string.Equals(p, categoryRootName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
            return 0;

        return Math.Max(0, parts.Length - (idx + 1));
    }

    private DriverCatalogInsight EnsureCommDriverCatalogEntry(ProjectMetrics metrics, string driverName)
    {
        if (metrics == null)
            return null;

        var key = string.IsNullOrWhiteSpace(driverName) ? "<unknown>" : driverName;
        if (!metrics.CommDriverCatalog.TryGetValue(key, out var entry))
        {
            entry = new DriverCatalogInsight { Name = key };
            metrics.CommDriverCatalog[key] = entry;
        }

        return entry;
    }

    private CommDriverStationInsight EnsureCommDriverStation(DriverCatalogInsight driverCatalog, string stationName)
    {
        if (driverCatalog == null || string.IsNullOrWhiteSpace(stationName))
            return null;

        if (!driverCatalog.Stations.TryGetValue(stationName, out var station))
        {
            station = new CommDriverStationInsight { Name = stationName };
            driverCatalog.Stations[stationName] = station;
        }

        return station;
    }

    private CommDriverOtherObjectInsight EnsureCommDriverOtherObject(ProjectMetrics metrics, IUANode node, string safePath, string typeName)
    {
        if (metrics == null || node == null)
            return null;

        var key = string.IsNullOrWhiteSpace(node.BrowseName) ? safePath : node.BrowseName;
        if (!metrics.CommDriverOtherObjects.TryGetValue(key, out var entry))
        {
            int childCount;
            try { childCount = node.Children?.Count ?? 0; }
            catch { childCount = 0; }

            entry = new CommDriverOtherObjectInsight
            {
                Name = node.BrowseName ?? string.Empty,
                Path = safePath,
                DetectedType = typeName,
                ChildCount = childCount
            };
            metrics.CommDriverOtherObjects[key] = entry;
        }

        return entry;
    }

    private void AddCommDriverIncomingReference(ProjectMetrics metrics, string tagPath, CommDriverTagReferenceInsight reference)
    {
        if (metrics == null || string.IsNullOrWhiteSpace(tagPath) || reference == null)
            return;

        if (!metrics.CommDriverTagIncomingReferencesByPath.TryGetValue(tagPath, out var list))
        {
            list = new List<CommDriverTagReferenceInsight>();
            metrics.CommDriverTagIncomingReferencesByPath[tagPath] = list;
        }

        // Bounded per tag; the true total is tracked separately in CommDriverTagUsageCountsByPath.
        if (list.Count < 25)
            list.Add(reference);
    }

    private void AddCommDriverUnresolvedCandidate(ProjectMetrics metrics, CommDriverUnresolvedCandidateInsight candidate)
    {
        if (metrics == null || candidate == null)
            return;

        metrics.CommDriverUnresolvedCandidateCount++;
        // Bounded emitted list; CommDriverUnresolvedCandidateCount holds the true total regardless of cap.
        if (metrics.CommDriverUnresolvedCandidates.Count < 200)
            metrics.CommDriverUnresolvedCandidates.Add(candidate);
    }

    private void AddIncomingRef(ProjectMetrics metrics, string targetPath, string sourcePath)
    {
        if (metrics == null || string.IsNullOrWhiteSpace(targetPath) || string.IsNullOrWhiteSpace(sourcePath))
            return;

        if (!metrics.IncomingRefsByPath.TryGetValue(targetPath, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            metrics.IncomingRefsByPath[targetPath] = set;
        }

        if (set.Count < 200)
            set.Add(sourcePath);
    }

    private void AddConverterUsage(ProjectMetrics metrics, string converterKey, string sourcePath)
    {
        if (metrics == null || string.IsNullOrWhiteSpace(converterKey) || string.IsNullOrWhiteSpace(sourcePath))
            return;

        if (!metrics.ConverterUsagesByPath.TryGetValue(converterKey, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            metrics.ConverterUsagesByPath[converterKey] = set;
        }

        if (set.Count < 300)
            set.Add(sourcePath);
    }

    // Canonicalizes a node path or a dynamic-link target so absolute ("/Objects/..."),
    // full ("Root/Objects/...") and resolved paths all reduce to the same key anchored
    // at "Objects/". A trailing path modifier like "@NodeId" is stripped so it matches
    // the plain node path. Relative targets ("../..") keep their form and rely on ResolvePath.
    private string CanonicalRefKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var p = path.Replace('\\', '/').Trim().TrimStart('/');
        int idx = p.IndexOf("Objects/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            p = p.Substring(idx);
        int at = p.IndexOf('@');
        if (at >= 0)
            p = p.Substring(0, at);
        return p.TrimEnd('/');
    }

    private bool PathHasAliasPlaceholder(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;
        int open = path.IndexOf('{');
        return open >= 0 && path.IndexOf('}') > open;
    }

    // Reads a NodePointer / NodeId-typed reference variable's target as a path string.
    // Returns null for non-reference variables. Covers MQTT subscribers, DataLogger targets, etc.
    private string TryGetNodePointerTargetPath(IUAVariable variable)
    {
        try
        {
            if (variable == null)
                return null;
            if (!string.Equals(ResolveVariableDataTypeName(variable), "NodeId", StringComparison.OrdinalIgnoreCase))
                return null;

            var uaValue = variable.Value;
            if (uaValue == null)
                return null;

            object raw = uaValue.Value;
            if (raw is NodeId nodeId)
            {
                try
                {
                    var target = InformationModel.Get(nodeId);
                    if (target != null)
                        return SafeNodePath(target);
                }
                catch { }
            }

            return raw?.ToString() ?? uaValue.ToString();
        }
        catch
        {
            return null;
        }
    }

    private string GenerateCommDriverTagDetailsJson(List<CommDriverTagInsight> tags, int maxItems)
    {
        var rows = (tags ?? new List<CommDriverTagInsight>())
            .OrderByDescending(t => t.UsageCount)
            .ThenByDescending(t => t.Depth)
            .ThenBy(t => t.DriverName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(row.Name));
            sb.Append("\",\"path\":\"");
            sb.Append(EscapeJson(row.Path));
            sb.Append("\",\"driver\":\"");
            sb.Append(EscapeJson(row.DriverName));
            sb.Append("\",\"stationName\":");
            sb.Append(string.IsNullOrWhiteSpace(row.StationName) ? "null" : "\"" + EscapeJson(row.StationName) + "\"");
            sb.Append(",\"variableType\":\"");
            sb.Append(EscapeJson(row.VariableType));
            sb.Append("\",\"dataType\":\"");
            sb.Append(EscapeJson(row.DataType));
            sb.Append("\",\"depth\":");
            sb.Append(row.Depth);
            // usageCount = total resolved incoming DynamicLink references to this tag (full project
            // count, not capped). usageSources below is the bounded, detailed sample of those refs.
            sb.Append(",\"usageCount\":");
            sb.Append(row.UsageCount);
            var incomingRefs = (row.IncomingReferences ?? new List<CommDriverTagReferenceInsight>())
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.SourcePath))
                .OrderBy(r => r.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.Append(",\"usageSourcesTotal\":");
            sb.Append(row.UsageCount);
            sb.Append(",\"usageSourcesEmitted\":");
            sb.Append(incomingRefs.Count);
            sb.Append(",\"usageSourcesTruncated\":");
            sb.Append(row.UsageCount > incomingRefs.Count ? "true" : "false");
            sb.Append(",\"usageSources\":[");
            for (int u = 0; u < incomingRefs.Count; u++)
            {
                if (u > 0)
                    sb.Append(",");
                var reference = incomingRefs[u];
                sb.Append("{\"path\":\"");
                sb.Append(EscapeJson(reference.SourcePath));
                sb.Append("\",\"category\":\"");
                sb.Append(EscapeJson(reference.SourceCategory));
                sb.Append("\",\"name\":");
                sb.Append(string.IsNullOrWhiteSpace(reference.SourceName) ? "null" : "\"" + EscapeJson(reference.SourceName) + "\"");
                sb.Append(",\"rawTarget\":\"");
                sb.Append(EscapeJson(reference.RawTarget));
                sb.Append("\",\"resolvedTargetPath\":\"");
                sb.Append(EscapeJson(reference.ResolvedTargetPath));
                sb.Append("\",\"mechanism\":\"");
                sb.Append(EscapeJson(reference.Mechanism));
                sb.Append("\",\"resolutionState\":\"");
                sb.Append(EscapeJson(reference.ResolutionState));
                sb.Append("\"}");
            }
            sb.Append("]");
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    // Concrete, unresolved DynamicLink candidates whose raw target text points under CommDrivers.
    // Listed separately from resolved references -- never merged into the usage/reference counts,
    // and never presented as proof that a target tag is missing or deleted.
    private string GenerateCommDriverUnresolvedCandidatesJson(List<CommDriverUnresolvedCandidateInsight> candidates, int maxItems)
    {
        var rows = (candidates ?? new List<CommDriverUnresolvedCandidateInsight>())
            .Where(c => c != null)
            .OrderBy(c => c.SourcePath, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            sb.Append("{\"sourcePath\":\"");
            sb.Append(EscapeJson(row.SourcePath));
            sb.Append("\",\"sourceCategory\":\"");
            sb.Append(EscapeJson(row.SourceCategory));
            sb.Append("\",\"sourceName\":");
            sb.Append(string.IsNullOrWhiteSpace(row.SourceName) ? "null" : "\"" + EscapeJson(row.SourceName) + "\"");
            sb.Append(",\"rawTarget\":\"");
            sb.Append(EscapeJson(row.RawTarget));
            sb.Append("\",\"canonicalTargetPath\":\"");
            sb.Append(EscapeJson(row.CanonicalTargetPath));
            sb.Append("\",\"mechanism\":\"");
            sb.Append(EscapeJson(row.Mechanism));
            sb.Append("\",\"resolutionState\":\"");
            sb.Append(EscapeJson(row.ResolutionState));
            sb.Append("\"}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateOpcUaEntitiesJson(List<OpcUaEntityInsight> entities, int maxItems)
    {
        var rows = (entities ?? new List<OpcUaEntityInsight>())
            .OrderBy(e => e.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(row.Name));
            sb.Append("\",\"path\":\"");
            sb.Append(EscapeJson(row.Path));
            sb.Append("\",\"kind\":\"");
            sb.Append(EscapeJson(row.Kind));
            sb.Append("\",\"hasClient\":");
            if (!string.Equals(row.Kind, "Subscription", StringComparison.Ordinal) || !row.HasClient.HasValue)
                sb.Append("null");
            else
                sb.Append(row.HasClient.Value ? "true" : "false");
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateCommDriverCatalogJson(Dictionary<string, DriverCatalogInsight> catalog, int maxItems)
    {
        var rows = (catalog ?? new Dictionary<string, DriverCatalogInsight>(StringComparer.OrdinalIgnoreCase))
            .Values
            .OrderByDescending(d => d.TagCount)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            var variableNames = row.VariableTypeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(60).ToList();
            var dataTypeNames = row.DataTypeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(60).ToList();

            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(row.Name));
            sb.Append("\",\"tagCount\":");
            sb.Append(row.TagCount);
            sb.Append(",\"variableTypeCount\":");
            sb.Append(row.VariableTypeNames.Count);
            sb.Append(",\"dataTypeCount\":");
            sb.Append(row.DataTypeNames.Count);

            sb.Append(",\"variableTypeNames\":[");
            for (int v = 0; v < variableNames.Count; v++)
            {
                if (v > 0)
                    sb.Append(",");
                sb.Append("\"");
                sb.Append(EscapeJson(variableNames[v]));
                sb.Append("\"");
            }
            sb.Append("]");

            sb.Append(",\"dataTypeNames\":[");
            for (int d = 0; d < dataTypeNames.Count; d++)
            {
                if (d > 0)
                    sb.Append(",");
                sb.Append("\"");
                sb.Append(EscapeJson(dataTypeNames[d]));
                sb.Append("\"");
            }
            sb.Append("]");

            var stationRows = (row.Stations ?? new Dictionary<string, CommDriverStationInsight>(StringComparer.OrdinalIgnoreCase))
                .Values
                .OrderByDescending(s => s.TagCount)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sb.Append(",\"stations\":[");
            for (int s = 0; s < stationRows.Count; s++)
            {
                if (s > 0)
                    sb.Append(",");

                var station = stationRows[s];
                var stationVariableNames = station.VariableTypeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(30).ToList();
                var stationDataTypeNames = station.DataTypeNames.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).Take(30).ToList();

                sb.Append("{\"name\":\"");
                sb.Append(EscapeJson(station.Name));
                sb.Append("\",\"path\":\"");
                sb.Append(EscapeJson(station.Path));
                sb.Append("\",\"tagCount\":");
                sb.Append(station.TagCount);
                sb.Append(",\"variableTypeCount\":");
                sb.Append(station.VariableTypeNames.Count);
                sb.Append(",\"dataTypeCount\":");
                sb.Append(station.DataTypeNames.Count);

                sb.Append(",\"variableTypeNames\":[");
                for (int v = 0; v < stationVariableNames.Count; v++)
                {
                    if (v > 0)
                        sb.Append(",");
                    sb.Append("\"");
                    sb.Append(EscapeJson(stationVariableNames[v]));
                    sb.Append("\"");
                }
                sb.Append("]");

                sb.Append(",\"dataTypeNames\":[");
                for (int d = 0; d < stationDataTypeNames.Count; d++)
                {
                    if (d > 0)
                        sb.Append(",");
                    sb.Append("\"");
                    sb.Append(EscapeJson(stationDataTypeNames[d]));
                    sb.Append("\"");
                }
                sb.Append("]");

                sb.Append("}");
            }
            sb.Append("]");

            sb.Append("}");
        }

        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateCommDriverOtherObjectsJson(Dictionary<string, CommDriverOtherObjectInsight> objects, int maxItems)
    {
        var rows = (objects ?? new Dictionary<string, CommDriverOtherObjectInsight>(StringComparer.OrdinalIgnoreCase))
            .Values
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(row.Name));
            sb.Append("\",\"path\":\"");
            sb.Append(EscapeJson(row.Path));
            sb.Append("\",\"detectedType\":\"");
            sb.Append(EscapeJson(row.DetectedType));
            sb.Append("\",\"childCount\":");
            sb.Append(row.ChildCount);
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateNetLogicCatalogJson(Dictionary<string, NetLogicTypeCatalogInsight> catalog, int maxTypes, int maxInstancesPerType)
    {
        var rows = (catalog ?? new Dictionary<string, NetLogicTypeCatalogInsight>(StringComparer.Ordinal))
            .Values
            .OrderByDescending(t => t.InstanceCount)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxTypes))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            var instances = (row.Instances ?? new List<NetLogicInstanceInfo>())
                .OrderBy(inst => inst.Path, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(1, maxInstancesPerType))
                .ToList();

            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(row.Name));
            sb.Append("\",\"instanceCount\":");
            sb.Append(row.InstanceCount);
            sb.Append(",\"resolutionMethod\":\"");
            sb.Append(EscapeJson(string.IsNullOrWhiteSpace(row.ResolutionMethod) ? "Unknown" : row.ResolutionMethod));
            sb.Append("\",\"sourceFile\":\"");
            sb.Append(EscapeJson(row.SourceFile ?? string.Empty));
            sb.Append("\",\"instances\":[");
            for (int j = 0; j < instances.Count; j++)
            {
                if (j > 0)
                    sb.Append(",");

                var inst = instances[j];
                sb.Append("{\"name\":\"");
                sb.Append(EscapeJson(inst.Name));
                sb.Append("\",\"path\":\"");
                sb.Append(EscapeJson(inst.Path));
                sb.Append("\",\"category\":\"");
                sb.Append(EscapeJson(inst.Category));
                sb.Append("\"}");
            }
            sb.Append("]}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateCodeFileInsightsJson(List<CodeFileInsight> files, int maxItems)
    {
        var rows = (files ?? new List<CodeFileInsight>())
            .OrderByDescending(f => f.ImpactScore)
            .ThenByDescending(f => f.CyclomaticComplexity)
            .ThenByDescending(f => f.CodeLines)
            .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            sb.Append("{\"path\":\"");
            sb.Append(EscapeJson(row.Path));
            sb.Append("\",\"impact\":");
            sb.Append(row.ImpactScore);
            sb.Append(",\"codeLines\":");
            sb.Append(row.CodeLines);
            sb.Append(",\"methods\":");
            sb.Append(row.MethodCount);
            sb.Append(",\"complexity\":");
            sb.Append(row.CyclomaticComplexity);
            sb.Append(",\"isNetLogic\":");
            sb.Append(row.IsNetLogicFile ? "true" : "false");
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateCodeFunctionUsageJson(List<CodeFunctionUsage> functions, int maxItems)
    {
        var rows = (functions ?? new List<CodeFunctionUsage>())
            .OrderByDescending(f => f.Count)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxItems))
            .ToList();

        var sb = new StringBuilder();
        sb.Append("[");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var row = rows[i];
            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(row.Name));
            sb.Append("\",\"count\":");
            sb.Append(row.Count);
            sb.Append("}");
        }
        sb.Append("]");
        return sb.ToString();
    }

    private string GenerateReportDataJson(ProjectMetrics metrics)
    {
        var screens = metrics.Screens.Values
            .OrderByDescending(s => s.NodeCount)
            .ToList();
        var typeCatalog = metrics.TypeCatalog.Values
            .OrderBy(t => t.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var allDynamicLinks = metrics.ProjectDynamicLinkDetails;
        var repeatedTargets = metrics.GlobalDynamicLinkTargetCounts
            .Where(t => t.Value > 1)
            .OrderByDescending(t => t.Value)
            .ThenBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var crossReferences = allDynamicLinks
            .Where(l => !string.IsNullOrWhiteSpace(l.TargetPath))
            .GroupBy(l => l.TargetPath)
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var nodeMax = screens.Count > 0 ? screens.Max(s => s.NodeCount) : 0;
        var nodeMin = screens.Count > 0 ? screens.Min(s => s.NodeCount) : 0;
        var nodeAvg = screens.Count > 0 ? screens.Average(s => s.NodeCount) : 0.0;
        var depthMax = screens.Count > 0 ? screens.Max(s => s.MaxDepth) : 0;
        var depthMin = screens.Count > 0 ? screens.Min(s => s.MaxDepth) : 0;
        var depthAvg = screens.Count > 0 ? screens.Average(s => s.MaxDepth) : 0.0;
        var complexRatio = metrics.TotalDynamicLinks > 0
            ? (metrics.TotalComplexDynamicLinks * 100.0 / metrics.TotalDynamicLinks)
            : 0.0;

        var sb = new StringBuilder();
        sb.Append("{");
        sb.Append("\"projectName\":\"");
        sb.Append(EscapeJson(Project.Current.BrowseName));
        sb.Append("\",");
        sb.Append("\"projectPath\":\"");
        sb.Append(EscapeJson(GetDisplayProjectPath()));
        sb.Append("\",");
        sb.Append("\"generatedAt\":\"");
        sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        sb.Append("\",");

        sb.Append("\"overview\":{");
        sb.Append("\"uiTotalNodes\":");
        sb.Append(metrics.UITotalNodes);
        sb.Append(",\"uiObjectNodes\":");
        sb.Append(metrics.UIObjectNodes);
        sb.Append(",\"commDriverTags\":");
        sb.Append(metrics.CommDriverTagCount);
        sb.Append(",\"screensCount\":");
        sb.Append(metrics.Screens.Count);
        sb.Append(",\"totalDynamicLinks\":");
        sb.Append(metrics.TotalDynamicLinks);
        sb.Append(",\"maxDepth\":");
        sb.Append(metrics.MaxUIDepth);
        sb.Append(",\"totalBrokenDynamicLinks\":");
        sb.Append(metrics.TotalBrokenDynamicLinks);
        sb.Append(",\"totalComplexDynamicLinks\":");
        sb.Append(metrics.TotalComplexDynamicLinks);
        sb.Append(",\"complexRatio\":");
        sb.Append(complexRatio.ToString("0.0", CultureInfo.InvariantCulture));
        sb.Append("},");

        sb.Append("\"screenStats\":{");
        sb.Append("\"nodeMax\":");
        sb.Append(nodeMax);
        sb.Append(",\"nodeMin\":");
        sb.Append(nodeMin);
        sb.Append(",\"nodeAvg\":");
        sb.Append(nodeAvg.ToString("0.0", CultureInfo.InvariantCulture));
        sb.Append(",\"depthMax\":");
        sb.Append(depthMax);
        sb.Append(",\"depthMin\":");
        sb.Append(depthMin);
        sb.Append(",\"depthAvg\":");
        sb.Append(depthAvg.ToString("0.0", CultureInfo.InvariantCulture));
        sb.Append("},");

        sb.Append("\"typeDistribution\":");
        sb.Append(GenerateTypeDistributionJson(metrics));

        var categoryKeys = GetOrderedCategoryKeys(metrics);

        sb.Append(",\"categoryMetrics\":{");
        for (int i = 0; i < categoryKeys.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var key = categoryKeys[i];
            metrics.CategoryNodeCounts.TryGetValue(key, out var categoryNodes);
            metrics.CategoryDynamicLinkCounts.TryGetValue(key, out var categoryDynamicLinks);
            metrics.CategoryNetLogicCounts.TryGetValue(key, out var categoryNetLogic);
            metrics.CategoryFolderCounts.TryGetValue(key, out var categoryFolders);
            metrics.CategoryObjectCounts.TryGetValue(key, out var categoryObjects);
            metrics.CategoryVariableCounts.TryGetValue(key, out var categoryVariables);

            sb.Append("\"");
            sb.Append(key);
            sb.Append("\":{");
            sb.Append("\"nodes\":");
            sb.Append(categoryNodes);
            sb.Append(",\"folders\":");
            sb.Append(categoryFolders);
            sb.Append(",\"objects\":");
            sb.Append(categoryObjects);
            sb.Append(",\"variables\":");
            sb.Append(categoryVariables);
            sb.Append(",\"dynamicLinks\":");
            sb.Append(categoryDynamicLinks);
            sb.Append(",\"netLogic\":");
            sb.Append(categoryNetLogic);
            sb.Append("}");
        }
        sb.Append("}");

        sb.Append(",\"crossCutting\":{");
        sb.Append("\"dynamicLinksTotal\":");
        sb.Append(metrics.TotalDynamicLinks);
        sb.Append(",\"netLogicTotal\":");
        sb.Append(metrics.TotalNetLogicNodes);
        sb.Append(",\"netLogicDesign\":");
        sb.Append(metrics.NetLogicDesignNodes);
        sb.Append(",\"netLogicRuntime\":");
        sb.Append(metrics.NetLogicRuntimeNodes);
        sb.Append(",\"dynamicLinksByCategory\":[");
        for (int i = 0; i < categoryKeys.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var key = categoryKeys[i];
            metrics.CategoryDynamicLinkCounts.TryGetValue(key, out var count);
            sb.Append("{\"category\":\"");
            sb.Append(key);
            sb.Append("\",\"count\":");
            sb.Append(count);
            sb.Append("}");
        }
        sb.Append("]");
        sb.Append(",\"netLogicByCategory\":[");
        for (int i = 0; i < categoryKeys.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var key = categoryKeys[i];
            metrics.CategoryNetLogicCounts.TryGetValue(key, out var count);
            sb.Append("{\"category\":\"");
            sb.Append(key);
            sb.Append("\",\"count\":");
            sb.Append(count);
            sb.Append("}");
        }
        sb.Append("]");
        sb.Append("}");

        sb.Append(",\"specialMetrics\":{");
        sb.Append("\"commdrivers\":{");
        sb.Append("\"drivers\":");
        sb.Append(metrics.CommDriverCount);
        sb.Append(",\"tags\":");
        sb.Append(metrics.CommDriverTagCount);
        sb.Append(",\"brokenDynamicLinks\":");
        sb.Append(metrics.CommDriverBrokenLinkCount);
        sb.Append(",\"unresolvedDynamicLinks\":");
        sb.Append(metrics.CommDriverUnresolvedLinkCount);
        sb.Append(",\"tagsWithIncomingRef\":");
        sb.Append(metrics.CommDriverTagsWithIncomingRef);
        sb.Append(",\"tagsWithoutIncomingRef\":");
        sb.Append(metrics.CommDriverTagsWithoutIncomingRef);
        sb.Append(",\"resolvedIncomingRefCount\":");
        sb.Append(metrics.CommDriverResolvedIncomingRefCount);
        sb.Append(",\"unresolvedCandidateCount\":");
        sb.Append(metrics.CommDriverUnresolvedCandidateCount);
        sb.Append(",\"driversWithoutTags\":");
        sb.Append(metrics.CommDriverDriversWithoutTags);
        sb.Append("},");
        sb.Append("\"opcua\":{");
        sb.Append("\"clients\":");
        sb.Append(metrics.OpcUaClientCount);
        sb.Append(",\"servers\":");
        sb.Append(metrics.OpcUaServerCount);
        sb.Append(",\"subscriptions\":");
        sb.Append(metrics.OpcUaSubscriptionCount);
        sb.Append(",\"brokenDynamicLinks\":");
        sb.Append(metrics.OpcUaBrokenLinkCount);
        sb.Append(",\"unresolvedDynamicLinks\":");
        sb.Append(metrics.OpcUaUnresolvedLinkCount);
        sb.Append(",\"subscriptionsWithoutClient\":");
        sb.Append(metrics.OpcUaSubscriptionsWithoutClient);
        sb.Append("},");
        sb.Append("\"converters\":{");
        sb.Append("\"converterNodes\":");
        sb.Append(metrics.ConverterNodeCount);
        sb.Append("},");
        sb.Append("\"alarms\":{");
        sb.Append("\"alarmNodes\":");
        sb.Append(metrics.AlarmNodeCount);
        sb.Append("},");
        sb.Append("\"recipes\":{");
        sb.Append("\"recipeNodes\":");
        sb.Append(metrics.RecipeNodeCount);
        sb.Append("},");
        sb.Append("\"datastores\":{");
        sb.Append("\"storeNodes\":");
        sb.Append(metrics.DataStoreNodeCount);
        sb.Append("},");
        sb.Append("\"loggers\":{");
        sb.Append("\"loggerNodes\":");
        sb.Append(metrics.LoggerNodeCount);
        sb.Append("},");
        sb.Append("\"reports\":{");
        sb.Append("\"reportNodes\":");
        sb.Append(metrics.ReportNodeCount);
        sb.Append("},");
        sb.Append("\"security\":{");
        sb.Append("\"users\":");
        sb.Append(metrics.SecurityUserCount);
        sb.Append(",\"groups\":");
        sb.Append(metrics.SecurityGroupCount);
        sb.Append(",\"roles\":");
        sb.Append(metrics.SecurityRoleCount);
        sb.Append("},");
        sb.Append("\"translations\":{");
        sb.Append("\"translationNodes\":");
        sb.Append(metrics.TranslationNodeCount);
        sb.Append(",\"locales\":");
        sb.Append(metrics.LocaleCount);
        sb.Append("},");
        sb.Append("\"retentivity\":{");
        sb.Append("\"retentivityNodes\":");
        sb.Append(metrics.RetentivityNodeCount);
        sb.Append("},");
        sb.Append("\"netlogic\":{");
        sb.Append("\"total\":");
        sb.Append(metrics.TotalNetLogicNodes);
        sb.Append(",\"design\":");
        sb.Append(metrics.NetLogicDesignNodes);
        sb.Append(",\"runtime\":");
        sb.Append(metrics.NetLogicRuntimeNodes);
        sb.Append(",\"uniqueTypes\":");
        sb.Append(metrics.NetLogicTypeCounts.Count);
        sb.Append(",\"mostRepeatedTypeInstances\":");
        sb.Append(metrics.NetLogicTypeCounts.Count > 0 ? metrics.NetLogicTypeCounts.Max(kv => kv.Value) : 0);
        sb.Append("}");
        sb.Append("}");

        sb.Append(",\"categoryDetails\":{");
        sb.Append("\"commdrivers\":{");
        sb.Append("\"summary\":{\"drivers\":");
        sb.Append(metrics.CommDriverCount);
        sb.Append(",\"tags\":");
        sb.Append(metrics.CommDriverTagCount);
        sb.Append("},\"anomalies\":{\"brokenDynamicLinks\":");
        sb.Append(metrics.CommDriverBrokenLinkCount);
        sb.Append(",\"unresolvedDynamicLinks\":");
        sb.Append(metrics.CommDriverUnresolvedLinkCount);
        sb.Append(",\"driversWithoutTags\":");
        sb.Append(metrics.CommDriverDriversWithoutTags);
        sb.Append("},\"referenceSummary\":{\"mechanism\":\"DynamicLink\"");
        sb.Append(",\"tagsWithIncomingRef\":");
        sb.Append(metrics.CommDriverTagsWithIncomingRef);
        sb.Append(",\"tagsWithoutIncomingRef\":");
        sb.Append(metrics.CommDriverTagsWithoutIncomingRef);
        sb.Append(",\"resolvedReferenceCount\":");
        sb.Append(metrics.CommDriverResolvedIncomingRefCount);
        sb.Append(",\"unresolvedCandidateCount\":");
        sb.Append(metrics.CommDriverUnresolvedCandidateCount);
        sb.Append(",\"unresolvedCandidatesEmitted\":");
        sb.Append(Math.Min(metrics.CommDriverUnresolvedCandidateCount, metrics.CommDriverUnresolvedCandidates.Count));
        sb.Append(",\"limitationNote\":\"");
        sb.Append(EscapeJson("Coverage is limited to DynamicLink references that resolve at design time via ResolvePath. NodePointer references, alias/pointer expansion, NetLogic source-code reads/writes, and recipe/alarm/DataLogger runtime-only access are not detected by this metric, so a tag shown without a detected incoming reference is not proven unused."));
        sb.Append("\"},\"unresolvedCandidates\":");
        sb.Append(GenerateCommDriverUnresolvedCandidatesJson(metrics.CommDriverUnresolvedCandidates, 200));
        sb.Append(",\"typeDistribution\":");
        sb.Append(GenerateCountDistributionJson(metrics.CommDriverTypeCounts, 30));
        sb.Append(",\"driverDetails\":");
        sb.Append(GenerateCommDriverCatalogJson(metrics.CommDriverCatalog, 60));
        sb.Append(",\"otherObjects\":");
        sb.Append(GenerateCommDriverOtherObjectsJson(metrics.CommDriverOtherObjects, 60));
        sb.Append(",\"tagDetails\":");
        sb.Append(GenerateCommDriverTagDetailsJson(metrics.CommDriverTagDetails, 250));
        sb.Append(",\"topNodes\":");
        sb.Append(GenerateNodeInsightsJson(metrics.CommDriverTopNodes, 40));
        sb.Append("},");
        sb.Append("\"opcua\":{");
        sb.Append("\"summary\":{\"clients\":");
        sb.Append(metrics.OpcUaClientCount);
        sb.Append(",\"servers\":");
        sb.Append(metrics.OpcUaServerCount);
        sb.Append(",\"subscriptions\":");
        sb.Append(metrics.OpcUaSubscriptionCount);
        sb.Append("},\"anomalies\":{\"brokenDynamicLinks\":");
        sb.Append(metrics.OpcUaBrokenLinkCount);
        sb.Append(",\"unresolvedDynamicLinks\":");
        sb.Append(metrics.OpcUaUnresolvedLinkCount);
        sb.Append(",\"subscriptionsWithoutClient\":");
        sb.Append(metrics.OpcUaSubscriptionsWithoutClient);
        sb.Append("},\"typeDistribution\":");
        sb.Append(GenerateCountDistributionJson(metrics.OpcUaTypeCounts, 30));
        sb.Append(",\"topNodes\":");
        sb.Append(GenerateNodeInsightsJson(metrics.OpcUaTopNodes, 40));
        sb.Append(",\"entities\":");
        sb.Append(GenerateOpcUaEntitiesJson(metrics.OpcUaEntities, 500));
        sb.Append("},");
        sb.Append("\"netlogic\":{");
        sb.Append("\"summary\":{\"total\":");
        sb.Append(metrics.TotalNetLogicNodes);
        sb.Append(",\"design\":");
        sb.Append(metrics.NetLogicDesignNodes);
        sb.Append(",\"runtime\":");
        sb.Append(metrics.NetLogicRuntimeNodes);
        sb.Append(",\"uniqueTypes\":");
        sb.Append(metrics.NetLogicTypeCounts.Count);
        sb.Append("},\"typeDistribution\":");
        sb.Append(GenerateCountDistributionJson(metrics.NetLogicTypeCounts, 40));
        sb.Append(",\"topNodes\":");
        sb.Append(GenerateNodeInsightsJson(metrics.NetLogicTopNodes, 40));
        sb.Append(",\"types\":");
        sb.Append(GenerateNetLogicCatalogJson(metrics.NetLogicCatalog, 200, 100));
        sb.Append("}");
        sb.Append(",\"converters\":{");
        sb.Append("\"summary\":{\"types\":");
        sb.Append(metrics.ConverterCatalog.Count);
        sb.Append(",\"totalUsages\":");
        sb.Append(metrics.ConverterCatalog.Sum(c => c.UsageCount));
        sb.Append(",\"other\":");
        sb.Append(metrics.ConverterOtherItems.Count);
        sb.Append("},\"items\":");
        sb.Append(GenerateConverterCatalogJson(metrics.ConverterCatalog, 200));
        sb.Append(",\"otherItems\":");
        sb.Append(GenerateConverterCatalogJson(metrics.ConverterOtherItems, 200));
        sb.Append("}");
        sb.Append(",\"alarms\":{");
        sb.Append("\"summary\":{\"instances\":");
        sb.Append(metrics.AlarmCatalog.Count);
        sb.Append(",\"types\":");
        sb.Append(metrics.AlarmTypeCatalog.Count);
        sb.Append(",\"withBrokenLinks\":");
        sb.Append(metrics.AlarmCatalog.Count(a => a.SourceLinks.Any(l => string.Equals(l.ResultKind, DynamicLinkResultKindBroken, StringComparison.Ordinal))));
        sb.Append("},\"items\":");
        sb.Append(GenerateAlarmCatalogJson(metrics.AlarmCatalog, 300));
        sb.Append(",\"typeItems\":");
        sb.Append(GenerateConverterCatalogJson(metrics.AlarmTypeCatalog, 100));
        sb.Append("}");
        sb.Append(",\"recipes\":{");
        sb.Append("\"summary\":{\"schemas\":");
        sb.Append(metrics.RecipeSchemas.Count);
        sb.Append("},\"items\":");
        sb.Append(GenerateRecipeSchemaJson(metrics.RecipeSchemas, 200));
        sb.Append("}");
        sb.Append(",\"datastores\":{");
        sb.Append("\"summary\":{\"stores\":");
        sb.Append(metrics.DataStoreCatalog.Count);
        sb.Append(",\"tables\":");
        sb.Append(metrics.DataStoreCatalog.Sum(d => d.Tables.Count));
        sb.Append("},\"items\":");
        sb.Append(GenerateDataStoreCatalogJson(metrics.DataStoreCatalog, 200));
        sb.Append("}");
        sb.Append(",\"loggers\":{");
        sb.Append("\"summary\":{\"loggers\":");
        sb.Append(metrics.LoggerCatalog.Count);
        sb.Append(",\"totalBindings\":");
        sb.Append(metrics.LoggerCatalog.Sum(l => l.Variables.Count));
        sb.Append("},\"items\":");
        sb.Append(GenerateLoggerCatalogJson(metrics.LoggerCatalog, 200));
        sb.Append("}");
        sb.Append(",\"security\":{");
        sb.Append("\"summary\":{\"users\":");
        sb.Append(metrics.SecurityUsers.Count);
        sb.Append(",\"groups\":");
        sb.Append(metrics.SecurityGroups.Count);
        sb.Append(",\"roles\":");
        sb.Append(metrics.SecurityRoles.Count);
        sb.Append("},\"users\":");
        sb.Append(GenerateConverterCatalogJson(metrics.SecurityUsers, 200));
        sb.Append(",\"groups\":");
        sb.Append(GenerateConverterCatalogJson(metrics.SecurityGroups, 200));
        sb.Append(",\"roles\":");
        sb.Append(GenerateConverterCatalogJson(metrics.SecurityRoles, 200));
        sb.Append("}");
        sb.Append(",\"translations\":{");
        sb.Append("\"summary\":{\"dictionaries\":");
        sb.Append(metrics.TranslationDictionaries.Count);
        sb.Append(",\"totalKeys\":");
        sb.Append(metrics.TranslationDictionaries.Sum(d => d.KeyCount));
        sb.Append(",\"locales\":");
        sb.Append(metrics.TranslationDictionaries.Count > 0 ? metrics.TranslationDictionaries.Max(d => d.Locales.Count) : 0);
        sb.Append("},\"items\":");
        sb.Append(GenerateTranslationDictionaryJson(metrics.TranslationDictionaries, 100));
        sb.Append("}");
        sb.Append(",\"reports\":{");
        sb.Append("\"summary\":{\"items\":");
        sb.Append(metrics.ReportCatalog.Count);
        sb.Append("},\"items\":");
        sb.Append(GenerateConverterCatalogJson(metrics.ReportCatalog, 200));
        sb.Append("}");
        sb.Append(",\"retentivity\":{");
        sb.Append("\"summary\":{\"items\":");
        sb.Append(metrics.RetentivityCatalog.Count);
        sb.Append("},\"items\":");
        sb.Append(GenerateConverterCatalogJson(metrics.RetentivityCatalog, 200));
        sb.Append("}");
        sb.Append(",\"mqtt\":");
        sb.Append(GenerateMqttCatalogJson(metrics));
        sb.Append("}");

        sb.Append(",\"solutionInsights\":{");
        sb.Append("\"csFiles\":");
        sb.Append(metrics.SolutionCsFileCount);
        sb.Append(",\"netLogicFiles\":");
        sb.Append(metrics.SolutionNetLogicFileCount);
        sb.Append(",\"totalCodeLines\":");
        sb.Append(metrics.SolutionCodeLineCount);
        sb.Append(",\"totalMethods\":");
        sb.Append(metrics.SolutionMethodCount);
        sb.Append(",\"totalComplexity\":");
        sb.Append(metrics.SolutionComplexityScore);
        sb.Append(",\"topFiles\":");
        sb.Append(GenerateCodeFileInsightsJson(metrics.SolutionCodeFiles, 30));
        sb.Append(",\"topFunctions\":");
        sb.Append(GenerateCodeFunctionUsageJson(metrics.SolutionTopFunctionCalls, 30));
        sb.Append("}");

        sb.Append(",\"screens\":[");
        for (int i = 0; i < screens.Count; i++)
        {
            var s = screens[i];
            if (i > 0)
                sb.Append(",");

            sb.Append("{");
            sb.Append("\"name\":\"");
            sb.Append(EscapeJson(s.Name));
            sb.Append("\",");
            sb.Append("\"fullPath\":\"");
            sb.Append(EscapeJson(s.FullPath));
            sb.Append("\",");
            sb.Append("\"baseType\":\"");
            sb.Append(EscapeJson(s.BaseType));
            sb.Append("\",");
            sb.Append("\"nodeCount\":");
            sb.Append(s.NodeCount);
            sb.Append(",\"maxDepth\":");
            sb.Append(s.MaxDepth);
            sb.Append(",\"dependenciesCount\":");
            sb.Append(s.Dependencies.Count);
            sb.Append(",\"uiDependenciesCount\":");
            sb.Append(s.DependencyKinds.Values.Count(k => k == "ui"));
            sb.Append(",\"datatypeDependenciesCount\":");
            sb.Append(s.DependencyKinds.Values.Count(k => k == "datatype"));
            sb.Append(",\"customTypedNodeCount\":");
            sb.Append(s.CustomTypedNodeCount);
            sb.Append(",\"baseTypedNodeCount\":");
            sb.Append(s.BaseTypedNodeCount);
            sb.Append(",\"dependencyCoveredNodeCount\":");
            sb.Append(s.DependencyNodeCounts.Values.Sum());
            sb.Append(",\"dynamicLinkCount\":");
            sb.Append(s.DynamicLinkCount);
            sb.Append(",\"complexDynamicLinkCount\":");
            sb.Append(s.ComplexDynamicLinkCount);
            sb.Append(",\"brokenDynamicLinkCount\":");
            sb.Append(s.BrokenDynamicLinkCount);

            var depKeys = new HashSet<string>(s.Dependencies);
            foreach (var key in s.DependencyTypeCounts.Keys)
                depKeys.Add(key);

            var deps = depKeys
                .Select(name => new
                {
                    Name = name,
                    Count = s.DependencyTypeCounts.ContainsKey(name) ? s.DependencyTypeCounts[name] : 0
                })
                .OrderByDescending(d => d.Count)
                .ThenBy(d => d.Name)
                .ToList();
            sb.Append(",\"dependencyTypeCounts\":[");
            for (int d = 0; d < deps.Count; d++)
            {
                if (d > 0)
                    sb.Append(",");
                var depName = deps[d].Name;
                sb.Append("{\"name\":\"");
                sb.Append(EscapeJson(depName));
                sb.Append("\",\"count\":");
                sb.Append(deps[d].Count);
                sb.Append(",\"kind\":\"");
                sb.Append(s.DependencyKinds.TryGetValue(depName, out var depKind) ? depKind : "ui");
                sb.Append("\"");
                sb.Append(",\"targetId\":\"");
                sb.Append(EscapeJson(MakeAnchorId("type", depName)));
                sb.Append("\"");
                sb.Append(",\"nodeCount\":");
                sb.Append(s.DependencyNodeCounts.ContainsKey(depName) ? s.DependencyNodeCounts[depName] : 0);
                sb.Append(",\"dynamicLinkCount\":");
                sb.Append(s.DependencyDynamicLinkCounts.ContainsKey(depName) ? s.DependencyDynamicLinkCounts[depName] : 0);
                sb.Append("}");
            }
            sb.Append("]");

            var targets = s.DynamicLinkTargetCounts
                .Where(t => t.Value > 1)
                .OrderByDescending(t => t.Value)
                .ToList();
            sb.Append(",\"topTargetPaths\":[");
            for (int t = 0; t < targets.Count; t++)
            {
                if (t > 0)
                    sb.Append(",");
                sb.Append("{\"path\":\"");
                sb.Append(EscapeJson(targets[t].Key));
                sb.Append("\",\"bindings\":");
                sb.Append(targets[t].Value);
                sb.Append("}");
            }
            sb.Append("]");
            sb.Append("}");
        }
        sb.Append("]");

        sb.Append(",\"types\":[");
        for (int i = 0; i < typeCatalog.Count; i++)
        {
            var t = typeCatalog[i];
            if (i > 0)
                sb.Append(",");

            sb.Append("{\"name\":\"");
            sb.Append(EscapeJson(t.Name));
            sb.Append("\",\"anchorId\":\"");
            sb.Append(EscapeJson(MakeAnchorId("type", t.Name)));
            sb.Append("\",\"kind\":\"");
            sb.Append(EscapeJson(t.Kind ?? "other"));
            sb.Append("\",\"namespaceUri\":\"");
            sb.Append(EscapeJson(t.NamespaceUri ?? string.Empty));
            sb.Append("\",\"path\":\"");
            sb.Append(EscapeJson(BuildTypeLocationPath(t.TypeNode)));
            sb.Append("\",\"inheritance\":\"");
            sb.Append(EscapeJson(BuildInheritanceChain(t.TypeNode)));
            sb.Append("\",\"screenCount\":");
            sb.Append(t.ScreenNames.Count);
            sb.Append(",\"usageCount\":");
            sb.Append(t.UsagePaths.Count);
            sb.Append(",\"contentNodeCount\":");
            sb.Append(t.ContentNodeCount);
            sb.Append(",\"contentVariableCount\":");
            sb.Append(t.ContentVariableCount);
            sb.Append(",\"contentDynamicLinkCount\":");
            sb.Append(t.ContentDynamicLinkCount);
            sb.Append(",\"internalDependencyCount\":");
            sb.Append(t.InternalDependencies.Count);
            sb.Append(",\"internalDependencies\":[");
            var sortedInternalDeps = t.InternalDependencies.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            for (int d = 0; d < sortedInternalDeps.Count; d++)
            {
                if (d > 0)
                    sb.Append(",");
                sb.Append("{\"name\":\"");
                sb.Append(EscapeJson(sortedInternalDeps[d]));
                sb.Append("\",\"targetId\":\"");
                sb.Append(EscapeJson(MakeAnchorId("type", sortedInternalDeps[d])));
                sb.Append("\"}");
            }
            sb.Append("],\"usagePaths\":[");
            var sortedUsagePaths = t.UsagePaths.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            for (int u = 0; u < sortedUsagePaths.Count; u++)
            {
                if (u > 0)
                    sb.Append(",");
                sb.Append("\"");
                sb.Append(EscapeJson(sortedUsagePaths[u]));
                sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]");

        sb.Append(",\"dynamicLinks\":{");
        sb.Append("\"total\":");
        sb.Append(metrics.TotalDynamicLinks);
        sb.Append(",\"simple\":");
        sb.Append(metrics.TotalDynamicLinks - metrics.TotalComplexDynamicLinks);
        sb.Append(",\"complex\":");
        sb.Append(metrics.TotalComplexDynamicLinks);
        sb.Append(",\"broken\":");
        sb.Append(metrics.TotalBrokenDynamicLinks);
        sb.Append(",\"unresolved\":");
        sb.Append(allDynamicLinks.Count(l => string.Equals(l.ResultKind, DynamicLinkResultKindUnresolved, StringComparison.Ordinal)));
        sb.Append(",\"items\":[");
        for (int i = 0; i < allDynamicLinks.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var item = allDynamicLinks[i];
            sb.Append("{\"screen\":\"");
            sb.Append(EscapeJson(item.ScreenName ?? string.Empty));
            sb.Append("\",\"sourcePath\":\"");
            sb.Append(EscapeJson(item.SourcePath));
            sb.Append("\",\"targetPath\":\"");
            sb.Append(EscapeJson(item.TargetPath));
            sb.Append("\",\"isComplex\":");
            sb.Append(item.IsComplex ? "true" : "false");
            sb.Append(",\"resultKind\":\"");
            sb.Append(EscapeJson(item.ResultKind ?? DynamicLinkResultKindOk));
            sb.Append("\"");
            sb.Append("}");
        }
        sb.Append("],\"repeatedTargets\":[");
        for (int i = 0; i < repeatedTargets.Count; i++)
        {
            if (i > 0)
                sb.Append(",");
            sb.Append("{\"targetPath\":\"");
            sb.Append(EscapeJson(repeatedTargets[i].Key));
            sb.Append("\",\"count\":");
            sb.Append(repeatedTargets[i].Value);
            sb.Append("}");
        }
        sb.Append("],\"crossReferences\":[");
        for (int i = 0; i < crossReferences.Count; i++)
        {
            if (i > 0)
                sb.Append(",");

            var group = crossReferences[i];
            var sources = group
                .Select(g => g.SourcePath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Take(100)
                .ToList();

            sb.Append("{\"targetPath\":\"");
            sb.Append(EscapeJson(group.Key));
            sb.Append("\",\"count\":");
            sb.Append(group.Count());
            sb.Append(",\"sources\":[");
            for (int s = 0; s < sources.Count; s++)
            {
                if (s > 0)
                    sb.Append(",");
                sb.Append("\"");
                sb.Append(EscapeJson(sources[s]));
                sb.Append("\"");
            }
            sb.Append("]}");
        }
        sb.Append("]}");

        AppendModelJson(sb, metrics);
        sb.Append(",\"modelSummary\":{\"typeCount\":");
        sb.Append(metrics.ModelTypes.Count);
        sb.Append(",\"maxDepth\":");
        sb.Append(metrics.ModelMaxDepth);
        sb.Append("}");
        sb.Append(",\"findings\":");
        sb.Append(BuildFindingsJson(metrics));

        sb.Append("}");
        return sb.ToString();
    }

    private string GetJsonPropertyRaw(JsonElement root, string propertyName, string fallbackRaw)
    {
        if (root.TryGetProperty(propertyName, out var prop))
            return prop.GetRawText();
        return fallbackRaw;
    }

    private long CalculateTotalUsagePaths(JsonElement root)
    {
        if (!root.TryGetProperty("types", out var types) || types.ValueKind != JsonValueKind.Array)
            return 0;

        long total = 0;
        foreach (var type in types.EnumerateArray())
        {
            if (!type.TryGetProperty("usageCount", out var usage))
                continue;

            if (usage.ValueKind == JsonValueKind.Number)
            {
                if (usage.TryGetInt64(out var i))
                    total += i;
                else
                    total += (long)usage.GetDouble();
            }
        }

        return total;
    }

    private string SaveSplitReportDataFiles(string assetsPath, string fullDataJson)
    {
        using var doc = JsonDocument.Parse(fullDataJson);
        var root = doc.RootElement;

        var projectNameRaw = GetJsonPropertyRaw(root, "projectName", "\"Project\"");
        var projectPathRaw = GetJsonPropertyRaw(root, "projectPath", "\"\"");
        var generatedAtRaw = GetJsonPropertyRaw(root, "generatedAt", "\"\"");
        var overviewRaw = GetJsonPropertyRaw(root, "overview", "{}");
        var screenStatsRaw = GetJsonPropertyRaw(root, "screenStats", "{}");
        var typeDistributionRaw = GetJsonPropertyRaw(root, "typeDistribution", "[]");
        var screensRaw = GetJsonPropertyRaw(root, "screens", "[]");
        var typesRaw = GetJsonPropertyRaw(root, "types", "[]");
        var dynamicLinksRaw = GetJsonPropertyRaw(root, "dynamicLinks", "{}");
        var categoryMetricsRaw = GetJsonPropertyRaw(root, "categoryMetrics", "{}");
        var findingsRaw = GetJsonPropertyRaw(root, "findings", "{}");
        var modelSummaryRaw = GetJsonPropertyRaw(root, "modelSummary", "{}");
        var crossCuttingRaw = GetJsonPropertyRaw(root, "crossCutting", "{}");
        var specialMetricsRaw = GetJsonPropertyRaw(root, "specialMetrics", "{}");
        var categoryDetailsRaw = GetJsonPropertyRaw(root, "categoryDetails", "{}");
        var solutionInsightsRaw = GetJsonPropertyRaw(root, "solutionInsights", "{}");
        var modelRaw = GetJsonPropertyRaw(root, "model", "{}");
        var commDriversDetailRaw = "{}";
        var opcUaDetailRaw = "{}";
        var netLogicDetailRaw = "{}";

        if (root.TryGetProperty("categoryDetails", out var categoryDetailsElement) && categoryDetailsElement.ValueKind == JsonValueKind.Object)
        {
            if (categoryDetailsElement.TryGetProperty("commdrivers", out var commDriversElement))
                commDriversDetailRaw = commDriversElement.GetRawText();
            if (categoryDetailsElement.TryGetProperty("opcua", out var opcUaElement))
                opcUaDetailRaw = opcUaElement.GetRawText();
            if (categoryDetailsElement.TryGetProperty("netlogic", out var netLogicElement))
                netLogicDetailRaw = netLogicElement.GetRawText();
        }

        var typesCount = root.TryGetProperty("types", out var typesEl) && typesEl.ValueKind == JsonValueKind.Array
            ? typesEl.GetArrayLength()
            : 0;
        var dynamicLinksComplex = root.TryGetProperty("dynamicLinks", out var dynamicLinksEl)
            && dynamicLinksEl.TryGetProperty("complex", out var complexEl)
            && complexEl.ValueKind == JsonValueKind.Number
            ? complexEl.GetInt32()
            : 0;
        var totalUsagePaths = CalculateTotalUsagePaths(root);

        var overviewSectionJson = "{\"overview\":" + overviewRaw + ",\"typeDistribution\":" + typeDistributionRaw + "}";
        var screensSectionJson = "{\"screenStats\":" + screenStatsRaw + ",\"screens\":" + screensRaw + "}";
        var dynamicLinksSectionJson = "{\"dynamicLinks\":" + dynamicLinksRaw + "}";
        var typesSectionJson = "{\"types\":" + typesRaw + "}";
        var modelSectionJson = "{\"model\":" + modelRaw + "}";
        var crossCuttingSectionJson = "{\"crossCutting\":" + crossCuttingRaw + ",\"categoryMetrics\":" + categoryMetricsRaw + ",\"solutionInsights\":" + solutionInsightsRaw + ",\"netLogicDetails\":" + netLogicDetailRaw + "}";
        var commDriversSectionJson = commDriversDetailRaw;
        var opcUaSectionJson = opcUaDetailRaw;

        var overviewSectionPath = Path.Combine(assetsPath, "report-section-overview.json");
        var screensSectionPath = Path.Combine(assetsPath, "report-section-screens.json");
        var dynamicLinksSectionPath = Path.Combine(assetsPath, "report-section-dynamic-links.json");
        var typesSectionPath = Path.Combine(assetsPath, "report-section-types.json");
        var modelSectionPath = Path.Combine(assetsPath, "report-section-model.json");
        var crossCuttingSectionPath = Path.Combine(assetsPath, "report-section-cross-cutting.json");
        var commDriversSectionPath = Path.Combine(assetsPath, "report-section-commdrivers.json");
        var opcUaSectionPath = Path.Combine(assetsPath, "report-section-opcua.json");
        var overviewSectionJsPath = Path.Combine(assetsPath, "report-section-overview.js");
        var screensSectionJsPath = Path.Combine(assetsPath, "report-section-screens.js");
        var dynamicLinksSectionJsPath = Path.Combine(assetsPath, "report-section-dynamic-links.js");
        var typesSectionJsPath = Path.Combine(assetsPath, "report-section-types.js");
        var modelSectionJsPath = Path.Combine(assetsPath, "report-section-model.js");
        var crossCuttingSectionJsPath = Path.Combine(assetsPath, "report-section-cross-cutting.js");
        var commDriversSectionJsPath = Path.Combine(assetsPath, "report-section-commdrivers.js");
        var opcUaSectionJsPath = Path.Combine(assetsPath, "report-section-opcua.js");

        File.WriteAllText(overviewSectionPath, overviewSectionJson, Encoding.UTF8);
        File.WriteAllText(screensSectionPath, screensSectionJson, Encoding.UTF8);
        File.WriteAllText(dynamicLinksSectionPath, dynamicLinksSectionJson, Encoding.UTF8);
        File.WriteAllText(typesSectionPath, typesSectionJson, Encoding.UTF8);
        File.WriteAllText(modelSectionPath, modelSectionJson, Encoding.UTF8);
        File.WriteAllText(crossCuttingSectionPath, crossCuttingSectionJson, Encoding.UTF8);
        File.WriteAllText(commDriversSectionPath, commDriversSectionJson, Encoding.UTF8);
        File.WriteAllText(opcUaSectionPath, opcUaSectionJson, Encoding.UTF8);
        File.WriteAllText(overviewSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.overview = " + overviewSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(screensSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.screens = " + screensSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(dynamicLinksSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.dynamicLinks = " + dynamicLinksSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(typesSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.types = " + typesSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(modelSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.model = " + modelSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(crossCuttingSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.crossCutting = " + crossCuttingSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(commDriversSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.commdrivers = " + commDriversSectionJson + ";", Encoding.UTF8);
        File.WriteAllText(opcUaSectionJsPath, "window.reportSectionData = window.reportSectionData || {}; window.reportSectionData.opcua = " + opcUaSectionJson + ";", Encoding.UTF8);

        var manifestBuilder = new StringBuilder();
        manifestBuilder.Append("{");
        manifestBuilder.Append("\"projectName\":");
        manifestBuilder.Append(projectNameRaw);
        manifestBuilder.Append(",\"projectPath\":");
        manifestBuilder.Append(projectPathRaw);
        manifestBuilder.Append(",\"generatedAt\":");
        manifestBuilder.Append(generatedAtRaw);
        manifestBuilder.Append(",\"overview\":");
        manifestBuilder.Append(overviewRaw);
        manifestBuilder.Append(",\"screenStats\":");
        manifestBuilder.Append(screenStatsRaw);
        manifestBuilder.Append(",\"modelSummary\":");
        manifestBuilder.Append(modelSummaryRaw);
        manifestBuilder.Append(",\"categoryMetrics\":");
        manifestBuilder.Append(categoryMetricsRaw);
        manifestBuilder.Append(",\"findings\":");
        manifestBuilder.Append(findingsRaw);
        manifestBuilder.Append(",\"crossCutting\":");
        manifestBuilder.Append(crossCuttingRaw);
        manifestBuilder.Append(",\"specialMetrics\":");
        manifestBuilder.Append(specialMetricsRaw);
        manifestBuilder.Append(",\"categoryDetails\":");
        manifestBuilder.Append(categoryDetailsRaw);
        manifestBuilder.Append(",\"solutionInsights\":");
        manifestBuilder.Append(solutionInsightsRaw);
        manifestBuilder.Append(",\"summary\":{");
        manifestBuilder.Append("\"typesCount\":");
        manifestBuilder.Append(typesCount);
        manifestBuilder.Append(",\"totalUsagePaths\":");
        manifestBuilder.Append(totalUsagePaths);
        manifestBuilder.Append(",\"dynamicLinksComplex\":");
        manifestBuilder.Append(dynamicLinksComplex);
        manifestBuilder.Append("}");
        manifestBuilder.Append(",\"sections\":{");
        manifestBuilder.Append("\"overview\":\"ProjectDiagnostics.assets/report-section-overview.json\",");
        manifestBuilder.Append("\"screens\":\"ProjectDiagnostics.assets/report-section-screens.json\",");
        manifestBuilder.Append("\"dynamicLinks\":\"ProjectDiagnostics.assets/report-section-dynamic-links.json\",");
        manifestBuilder.Append("\"types\":\"ProjectDiagnostics.assets/report-section-types.json\",");
        manifestBuilder.Append("\"model\":\"ProjectDiagnostics.assets/report-section-model.json\",");
        manifestBuilder.Append("\"crossCutting\":\"ProjectDiagnostics.assets/report-section-cross-cutting.json\",");
        manifestBuilder.Append("\"commdrivers\":\"ProjectDiagnostics.assets/report-section-commdrivers.json\",");
        manifestBuilder.Append("\"opcua\":\"ProjectDiagnostics.assets/report-section-opcua.json\"");
        manifestBuilder.Append("},\"sectionsScript\":{");
        manifestBuilder.Append("\"overview\":\"ProjectDiagnostics.assets/report-section-overview.js\",");
        manifestBuilder.Append("\"screens\":\"ProjectDiagnostics.assets/report-section-screens.js\",");
        manifestBuilder.Append("\"dynamicLinks\":\"ProjectDiagnostics.assets/report-section-dynamic-links.js\",");
        manifestBuilder.Append("\"types\":\"ProjectDiagnostics.assets/report-section-types.js\",");
        manifestBuilder.Append("\"model\":\"ProjectDiagnostics.assets/report-section-model.js\",");
        manifestBuilder.Append("\"crossCutting\":\"ProjectDiagnostics.assets/report-section-cross-cutting.js\",");
        manifestBuilder.Append("\"commdrivers\":\"ProjectDiagnostics.assets/report-section-commdrivers.js\",");
        manifestBuilder.Append("\"opcua\":\"ProjectDiagnostics.assets/report-section-opcua.js\"");
        manifestBuilder.Append("}");
        manifestBuilder.Append("}");

        var manifestJson = manifestBuilder.ToString();
        File.WriteAllText(Path.Combine(assetsPath, "report-manifest.json"), manifestJson, Encoding.UTF8);
        return manifestJson;
    }

    private string GetReportTemplateSourcePath()
    {
        var projectDir = Project.Current.ProjectDirectory;

        // Some runtimes return .../SYSX/ProjectFiles as project directory,
        // others return .../SYSX. Support both layouts.
        var candidates = new[]
        {
            Path.Combine(projectDir, "ReportTemplateAssets"),
            Path.Combine(projectDir, "NetSolution", "ReportTemplateAssets"),
            Path.Combine(projectDir, "ProjectFiles", "NetSolution", "ReportTemplateAssets")
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate))
                return candidate;
        }

        // Default to the first expected layout.
        return candidates[0];
    }

    private void SaveReportAssets(string outputRoot, ProjectMetrics metrics)
    {
        var assetsPath = SherlockReportAssets.EmitAssets(outputRoot);

        var dataJson = GenerateReportDataJson(metrics);
        var manifestJson = SaveSplitReportDataFiles(assetsPath, dataJson);
        File.WriteAllText(Path.Combine(assetsPath, "report-data.json"), dataJson, Encoding.UTF8);
        File.WriteAllText(Path.Combine(assetsPath, "report-data.js"), "window.reportDataManifest = " + manifestJson + ";", Encoding.UTF8);
    }

    private void EnsureReportShellExists(string reportPath)
    {
        if (File.Exists(reportPath))
            return;

        var templatePath = Path.Combine(GetReportTemplateSourcePath(), "report-template.html");
        string html;

        if (File.Exists(templatePath))
        {
            var template = File.ReadAllText(templatePath);
            html = template.Replace("{{BODY_CONTENT}}", "<div id=\"app\"></div>");
        }
        else
        {
            html = "<!DOCTYPE html><html><head><meta charset=\"UTF-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\"><title>FT Optix Project Analysis</title><link rel=\"stylesheet\" href=\"ProjectDiagnostics.assets/report.css\"></head><body><div id=\"app\"></div><script src=\"ProjectDiagnostics.assets/report-data.js\"></script><script src=\"ProjectDiagnostics.assets/report.js\"></script></body></html>";
        }

        File.WriteAllText(reportPath, html, Encoding.UTF8);
    }

    private void SaveReportDataOnly(ProjectMetrics metrics)
    {
        try
        {
            string outputRoot = SherlockReportAssets.GetDiagnosticOutputRoot();
            string reportPath = Path.Combine(outputRoot, "ProjectDiagnostics.html");

            SaveReportAssets(outputRoot, metrics);
            SherlockReportAssets.EnsureShell(outputRoot, "ProjectDiagnostics.html");
            Log.Info(DiagnosticTag, $"Report generated at: {reportPath}");
            TryOpenReport(reportPath);
        }
        catch (Exception ex)
        {
            Log.Warning(DiagnosticTag, $"Could not save report data: {ex.Message}");
        }
    }

    private void TryOpenReport(string reportPath)
    {
        try
        {
            if (string.IsNullOrEmpty(reportPath) || !File.Exists(reportPath))
                return;

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = reportPath,
                UseShellExecute = true
            });
            Log.Info(DiagnosticTag, "Opening report in the default browser...");
        }
        catch (Exception ex)
        {
            Log.Warning(DiagnosticTag, $"Could not auto-open the report: {ex.Message}");
        }
    }

    private class ModelFieldInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
    }

    private class ModelTypeInfo
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string SuperType { get; set; }
        public string Path { get; set; }
        public int UsageCount { get; set; }
        public List<ModelFieldInfo> Fields { get; set; } = new List<ModelFieldInfo>();
        public List<string> UsagePaths { get; set; } = new List<string>();
    }

    private class ConverterCatalogInfo
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; } = "Converter";
        public int UsageCount { get; set; }
        public List<string> UsagePaths { get; set; } = new List<string>();
    }

    private class AlarmSourceLinkInfo
    {
        public string FieldName { get; set; }
        public string Target { get; set; }
        public string ResultKind { get; set; } = DynamicLinkResultKindOk;
    }

    private class AlarmInstanceInfo
    {
        public string Name { get; set; }
        public string TypeName { get; set; }
        public string Path { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        public List<AlarmSourceLinkInfo> SourceLinks { get; set; } = new List<AlarmSourceLinkInfo>();
        public List<string> IncomingRefs { get; set; } = new List<string>();
    }

    private class RecipeSchemaInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string TargetNode { get; set; }
        public string Store { get; set; }
        public string Status { get; set; }
        public List<string> MetadataFlags { get; set; } = new List<string>();
        public List<string> IncomingRefs { get; set; } = new List<string>();
    }

    private class DataStoreTableInfo
    {
        public string Name { get; set; }
        public int ColumnCount { get; set; }
    }

    private class DataStoreInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string TypeName { get; set; }
        public List<DataStoreTableInfo> Tables { get; set; } = new List<DataStoreTableInfo>();
        public List<string> IncomingRefs { get; set; } = new List<string>();
    }

    private class LoggerInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Store { get; set; }
        public List<AlarmSourceLinkInfo> Variables { get; set; } = new List<AlarmSourceLinkInfo>();
        public List<string> IncomingRefs { get; set; } = new List<string>();
    }

    private class MqttBrokerInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int ListenerPort { get; set; }
        public uint MaxConnections { get; set; }
        public bool RetainAvailable { get; set; }
        public bool DataPersistence { get; set; }
        public bool AnonymousAuthentication { get; set; }
        public bool Running { get; set; }
    }

    private class MqttPublisherInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Topic { get; set; }
        public string QoS { get; set; }
        public bool Retain { get; set; }
        public string DataSourcePath { get; set; }
        public List<string> Variables { get; set; } = new List<string>();
    }

    private class MqttSubscriptionInfo
    {
        public string Topic { get; set; }
        public string QoS { get; set; }
        public string TargetPath { get; set; }
        public List<string> Variables { get; set; } = new List<string>();
    }

    private class MqttSubscriberInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public List<MqttSubscriptionInfo> Subscriptions { get; set; } = new List<MqttSubscriptionInfo>();
    }

    private class MqttClientInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string BrokerAddress { get; set; }
        public int BrokerPort { get; set; }
        public string ClientId { get; set; }
        public bool SslEnabled { get; set; }
        public string Status { get; set; }
        public List<MqttPublisherInfo> Publishers { get; set; } = new List<MqttPublisherInfo>();
        public List<MqttSubscriberInfo> Subscribers { get; set; } = new List<MqttSubscriberInfo>();
    }

    private class TranslationLocaleStats
    {
        public string Locale { get; set; }
        public int TranslatedCount { get; set; }
        public int MissingCount { get; set; }
        public List<string> MissingKeys { get; set; } = new List<string>();
    }

    private class TranslationDictionaryInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int KeyCount { get; set; }
        public List<string> Locales { get; set; } = new List<string>();
        public List<TranslationLocaleStats> LocaleStats { get; set; } = new List<TranslationLocaleStats>();
    }

    private class ModelVariableInfo
    {
        public string Name { get; set; }
        public string DataType { get; set; }
        public string Path { get; set; }
        public string LinkTarget { get; set; }
        public string LinkKind { get; set; }
        public bool IsComplex { get; set; }
        public List<string> IncomingRefs { get; set; } = new List<string>();
    }

    private class ModelTreeNodeInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Kind { get; set; } = "object";
        public string DataType { get; set; }
        public string LinkTarget { get; set; }
        public string LinkKind { get; set; }
        public bool IsComplex { get; set; }
        public List<string> IncomingRefs { get; set; } = new List<string>();
        public List<ModelTreeNodeInfo> Children { get; set; } = new List<ModelTreeNodeInfo>();
    }

    private class ModelObjectInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string TypeName { get; set; }
        public int NodeCount { get; set; }
        public int VariableCount { get; set; }
        public int ObjectCount { get; set; }
        public int MaxDepth { get; set; }
        public int DynamicLinkCount { get; set; }
        public HashSet<string> DataTypes { get; set; } = new HashSet<string>(StringComparer.Ordinal);
        public List<ModelVariableInfo> Variables { get; set; } = new List<ModelVariableInfo>();
    }

    private class ProjectMetrics
    {
        public int TotalNodes { get; set; }
        public string UIRootName { get; set; }
        public int UITotalNodes { get; set; }
        public int UIObjectNodes { get; set; }
        public int UIFolderCount { get; set; }
        public int MaxUIDepth { get; set; }
        public int DerivedObjectCount { get; set; }
        public int MaxInheritanceDepth { get; set; }

        // Model category deep-dive
        public int ModelMaxDepth { get; set; }
        public int ModelStructureTypeCount { get; set; }
        public int ModelEnumTypeCount { get; set; }
        public Dictionary<string, int> ModelDataTypeDistribution { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> ModelTypeUsage { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
        public List<ModelTypeInfo> ModelTypes { get; set; } = new List<ModelTypeInfo>();
        public List<ModelObjectInfo> ModelObjects { get; set; } = new List<ModelObjectInfo>();
        public List<ModelTreeNodeInfo> ModelTree { get; set; } = new List<ModelTreeNodeInfo>();
        public List<ConverterCatalogInfo> ConverterCatalog { get; set; } = new List<ConverterCatalogInfo>();
        // Non-converter definitions found under the Converters folder (e.g. an Enumeration or
        // DataType that conceptually belongs to Model but was physically placed here in the project).
        public List<ConverterCatalogInfo> ConverterOtherItems { get; set; } = new List<ConverterCatalogInfo>();
        // Alarms category deep-dive: custom alarm ObjectTypes and configured alarm instances.
        public List<AlarmInstanceInfo> AlarmCatalog { get; set; } = new List<AlarmInstanceInfo>();
        public List<ConverterCatalogInfo> AlarmTypeCatalog { get; set; } = new List<ConverterCatalogInfo>();
        // Recipes / DataStores / Loggers deep-dives.
        public List<RecipeSchemaInfo> RecipeSchemas { get; set; } = new List<RecipeSchemaInfo>();
        public List<DataStoreInfo> DataStoreCatalog { get; set; } = new List<DataStoreInfo>();
        public List<LoggerInfo> LoggerCatalog { get; set; } = new List<LoggerInfo>();
        // Security deep-dive: Users/Groups/Roles catalogs (reuses ConverterCatalogInfo shape;
        // UsageCount/UsagePaths hold incoming cross-references instead of type usages).
        public List<ConverterCatalogInfo> SecurityUsers { get; set; } = new List<ConverterCatalogInfo>();
        public List<ConverterCatalogInfo> SecurityGroups { get; set; } = new List<ConverterCatalogInfo>();
        public List<ConverterCatalogInfo> SecurityRoles { get; set; } = new List<ConverterCatalogInfo>();
        // Translations deep-dive: per-locale completion stats.
        public List<TranslationDictionaryInfo> TranslationDictionaries { get; set; } = new List<TranslationDictionaryInfo>();
        // Reports / Retentivity: generic catalogs (no fixed Types/Instances shape).
        public List<ConverterCatalogInfo> ReportCatalog { get; set; } = new List<ConverterCatalogInfo>();
        public List<ConverterCatalogInfo> RetentivityCatalog { get; set; } = new List<ConverterCatalogInfo>();
        // MQTT deep-dive: Broker + Client(s) with their Publisher/Subscriber hierarchy.
        public MqttBrokerInfo MqttBroker { get; set; }
        public List<MqttClientInfo> MqttClients { get; set; } = new List<MqttClientInfo>();

        public int TotalDynamicLinks { get; set; }
        public int TotalComplexDynamicLinks { get; set; }
        public int TotalBrokenDynamicLinks { get; set; }
        public int TotalNetLogicNodes { get; set; }
        public int NetLogicDesignNodes { get; set; }
        public int NetLogicRuntimeNodes { get; set; }
        public int CommDriverCount { get; set; }
        public int CommDriverTagCount { get; set; }
        public int OpcUaClientCount { get; set; }
        public int OpcUaServerCount { get; set; }
        public int OpcUaSubscriptionCount { get; set; }
        public int ConverterNodeCount { get; set; }
        public int AlarmNodeCount { get; set; }
        public int RecipeNodeCount { get; set; }
        public int DataStoreNodeCount { get; set; }
        public int LoggerNodeCount { get; set; }
        public int ReportNodeCount { get; set; }
        public int SecurityUserCount { get; set; }
        public int SecurityGroupCount { get; set; }
        public int SecurityRoleCount { get; set; }
        public int TranslationNodeCount { get; set; }
        public int LocaleCount { get; set; }
        public int RetentivityNodeCount { get; set; }
        public int CommDriverBrokenLinkCount { get; set; }
        public int CommDriverUnresolvedLinkCount { get; set; }
        public int CommDriverTagsWithIncomingRef { get; set; }
        public int CommDriverTagsWithoutIncomingRef { get; set; }
        public int CommDriverResolvedIncomingRefCount { get; set; }
        public int CommDriverUnresolvedCandidateCount { get; set; }
        public int CommDriverDriversWithoutTags { get; set; }
        public int OpcUaBrokenLinkCount { get; set; }
        public int OpcUaUnresolvedLinkCount { get; set; }
        public int OpcUaSubscriptionsWithoutClient { get; set; }
        public int SolutionCsFileCount { get; set; }
        public int SolutionNetLogicFileCount { get; set; }
        public int SolutionCodeLineCount { get; set; }
        public int SolutionMethodCount { get; set; }
        public int SolutionComplexityScore { get; set; }

        public Dictionary<string, int> UITypeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CategoryNodeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CategoryDynamicLinkCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CategoryNetLogicCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CategoryFolderCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CategoryObjectCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CategoryVariableCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, ScreenInfo> Screens { get; set; } = new Dictionary<string, ScreenInfo>();
        public Dictionary<string, DriverInfo> Drivers { get; set; } = new Dictionary<string, DriverInfo>();
        public Dictionary<string, TypeMapInfo> TypeCatalog { get; set; } = new Dictionary<string, TypeMapInfo>();
        public Dictionary<string, int> TypeNodeWeightCache { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, List<string>> TypeUsageMap { get; set; } = new Dictionary<string, List<string>>();
        public Dictionary<string, HashSet<string>> InterdependencyMap { get; set; } = new Dictionary<string, HashSet<string>>();
        public Dictionary<string, int> GlobalDynamicLinkTargetCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> CommDriverTypeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> OpcUaTypeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> NetLogicTypeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, NetLogicTypeCatalogInsight> NetLogicCatalog { get; set; } = new Dictionary<string, NetLogicTypeCatalogInsight>(StringComparer.Ordinal);
        // ClassName -> declaring .cs file, for BaseNetLogic-derived classes found under NetSolution.
        public Dictionary<string, string> NetLogicClassNames { get; set; } = new Dictionary<string, string>(StringComparer.Ordinal);
        public Dictionary<string, int> CommDriverTagByDriver { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, DriverCatalogInsight> CommDriverCatalog { get; set; } = new Dictionary<string, DriverCatalogInsight>(StringComparer.OrdinalIgnoreCase);
        // Non-CommunicationDriver objects found directly under the CommDrivers folder (e.g. NetLogic
        // helper objects). Tracked and surfaced separately -- never counted or shown as real drivers.
        public Dictionary<string, CommDriverOtherObjectInsight> CommDriverOtherObjects { get; set; } = new Dictionary<string, CommDriverOtherObjectInsight>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> CommDriverTagUsageCountsByPath { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<CommDriverTagReferenceInsight>> CommDriverTagIncomingReferencesByPath { get; set; } = new Dictionary<string, List<CommDriverTagReferenceInsight>>(StringComparer.OrdinalIgnoreCase);
        public List<CommDriverUnresolvedCandidateInsight> CommDriverUnresolvedCandidates { get; set; } = new List<CommDriverUnresolvedCandidateInsight>();
        public Dictionary<string, HashSet<string>> IncomingRefsByPath { get; set; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HashSet<string>> ConverterUsagesByPath { get; set; } = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        public List<CommDriverTagInsight> CommDriverTagDetails { get; set; } = new List<CommDriverTagInsight>();
        public List<DynamicLinkInfo> ProjectDynamicLinkDetails { get; set; } = new List<DynamicLinkInfo>();
        public List<CategoryNodeInsight> CommDriverTopNodes { get; set; } = new List<CategoryNodeInsight>();
        public List<CategoryNodeInsight> OpcUaTopNodes { get; set; } = new List<CategoryNodeInsight>();
        public List<OpcUaEntityInsight> OpcUaEntities { get; set; } = new List<OpcUaEntityInsight>();
        public List<CategoryNodeInsight> NetLogicTopNodes { get; set; } = new List<CategoryNodeInsight>();
        public List<CodeFileInsight> SolutionCodeFiles { get; set; } = new List<CodeFileInsight>();
        public List<CodeFunctionUsage> SolutionTopFunctionCalls { get; set; } = new List<CodeFunctionUsage>();
    }

    private class ScreenInfo
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public string DeclaredType { get; set; }
        public string BaseType { get; set; }
        public int RootDepth { get; set; }
        public int NodeCount { get; set; }
        public int MaxDepth { get; set; }
        public int DerivedNodeCount { get; set; }
        public int CustomTypedNodeCount { get; set; }
        public int BaseTypedNodeCount { get; set; }
        public int DynamicLinkCount { get; set; }
        public int ComplexDynamicLinkCount { get; set; }
        public int BrokenDynamicLinkCount { get; set; }
        public int RepeatedTargetDynamicLinks { get; set; }

        public HashSet<string> Dependencies { get; set; } = new HashSet<string>();
        public Dictionary<string, string> DependencyKinds { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> DependencyTypeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, string> DependencyTypeNamespaces { get; set; } = new Dictionary<string, string>();
        public Dictionary<string, int> DependencyNodeCounts { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> DependencyDynamicLinkCounts { get; set; } = new Dictionary<string, int>();
        public HashSet<string> ExpandedDependencyTypeNodeIds { get; set; } = new HashSet<string>();
        public Dictionary<string, int> ElementTypes { get; set; } = new Dictionary<string, int>();
        public Dictionary<string, int> DynamicLinkTargetCounts { get; set; } = new Dictionary<string, int>();
        public List<DynamicLinkInfo> DynamicLinkDetails { get; set; } = new List<DynamicLinkInfo>();
    }

    private class DynamicLinkInfo
    {
        public string ScreenName { get; set; }
        public string SourceNode { get; set; }
        public string SourcePath { get; set; }
        public string TargetPath { get; set; }
        public bool IsComplex { get; set; }
        public string ResultKind { get; set; } = DynamicLinkResultKindOk;
        public string Category { get; set; }
    }

    private class TypeMapInfo
    {
        public string Name { get; set; }
        public string Kind { get; set; }
        public string NamespaceUri { get; set; }
        public IUANode TypeNode { get; set; }
        public int ContentNodeCount { get; set; }
        public int ContentVariableCount { get; set; }
        public int ContentDynamicLinkCount { get; set; }
        public bool IsAnalyzed { get; set; }
        public HashSet<string> UsagePaths { get; set; } = new HashSet<string>();
        public HashSet<string> ScreenNames { get; set; } = new HashSet<string>();
        public HashSet<string> InternalDependencies { get; set; } = new HashSet<string>();
    }

    private class DriverInfo
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public int NodeCount { get; set; }
        public int TagCount { get; set; }
        public HashSet<string> TagNames { get; set; } = new HashSet<string>();
    }

    private class CategoryNodeInsight
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string TypeName { get; set; }
        public int FanOut { get; set; }
        public bool HasDynamicLink { get; set; }
        public string ResultKind { get; set; }
        public int Score { get; set; }
    }

    private class CommDriverTagInsight
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string DriverName { get; set; }
        // First-level child folder/object name under the driver (e.g. "CLX1"), when the project
        // tree has one. Null when the tag sits directly under the driver with no such grouping.
        public string StationName { get; set; }
        public string VariableType { get; set; }
        public string DataType { get; set; }
        public int Depth { get; set; }
        public int UsageCount { get; set; }
        public List<CommDriverTagReferenceInsight> IncomingReferences { get; set; } = new List<CommDriverTagReferenceInsight>();
    }

    // A single resolved incoming DynamicLink reference to a CommDriver tag: the link is owned by
    // (set on) SourcePath and its ResolvePath target is this tag. Mechanism/ResolutionState are
    // explicit so the frontend never has to guess what was and wasn't detected.
    private class CommDriverTagReferenceInsight
    {
        public string SourcePath { get; set; }
        public string SourceCategory { get; set; }
        // Owning variable/property BrowseName of the source node, when the source is a variable.
        public string SourceName { get; set; }
        public string RawTarget { get; set; }
        public string ResolvedTargetPath { get; set; }
        public string Mechanism { get; set; } = "DynamicLink";
        public string ResolutionState { get; set; } = "Resolved";
    }

    // A DynamicLink whose raw target text points under CommDrivers but which ResolvePath could not
    // resolve to a project node. This is a candidate only: FT Optix could not resolve it at design
    // time for reasons unrelated to tag existence, so it must never be presented as proof that the
    // referenced tag is missing or deleted.
    private class CommDriverUnresolvedCandidateInsight
    {
        public string SourcePath { get; set; }
        public string SourceCategory { get; set; }
        public string SourceName { get; set; }
        public string RawTarget { get; set; }
        public string CanonicalTargetPath { get; set; }
        public string Mechanism { get; set; } = "DynamicLink";
        public string ResolutionState { get; set; } = "UnresolvedCandidate";
    }

    private class OpcUaEntityInsight
    {
        public string Name { get; set; }
        public string Path { get; set; }
        // "Client", "Server", or "Subscription".
        public string Kind { get; set; }
        // Subscription-only; null when the owning Client could not be reliably resolved.
        public bool? HasClient { get; set; }
    }

    private class DriverCatalogInsight
    {
        public string Name { get; set; }
        public int TagCount { get; set; }
        public HashSet<string> VariableTypeNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DataTypeNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Station/device-like child nodes found directly under this driver in the project tree.
        // Empty when no such grouping level exists -- the frontend falls back to driver-level totals.
        public Dictionary<string, CommDriverStationInsight> Stations { get; set; } = new Dictionary<string, CommDriverStationInsight>(StringComparer.OrdinalIgnoreCase);
    }

    // A station/device-like child node under a real CommunicationDriver, derived only from the
    // actual project tree (first-level child folder/object of the driver). No semantic meaning
    // (e.g. "unused"/"invalid") is inferred -- only structure and counts.
    private class CommDriverStationInsight
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public int TagCount { get; set; }
        public HashSet<string> VariableTypeNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DataTypeNames { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    // A non-CommunicationDriver object found directly under the CommDrivers folder. Reported for
    // transparency/review -- never counted as a driver and never assigned a risk/severity score.
    private class CommDriverOtherObjectInsight
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string DetectedType { get; set; }
        public int ChildCount { get; set; }
    }

    private class NetLogicTypeCatalogInsight
    {
        public string Name { get; set; }
        public int InstanceCount { get; set; }
        public List<NetLogicInstanceInfo> Instances { get; set; } = new List<NetLogicInstanceInfo>();
        // How Name was resolved: TypeDefinition, ClassNameMatch, BrowseNameFallback, or Unknown.
        public string ResolutionMethod { get; set; } = "Unknown";
        // Relative NetSolution path of the declaring class file, when known (ClassNameMatch only).
        public string SourceFile { get; set; } = string.Empty;
    }

    private class NetLogicInstanceInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Category { get; set; }
    }

    private class CodeFileInsight
    {
        public string Path { get; set; }
        public int TotalLines { get; set; }
        public int CodeLines { get; set; }
        public int ClassCount { get; set; }
        public int MethodCount { get; set; }
        public int CyclomaticComplexity { get; set; }
        public int ImpactScore { get; set; }
        public bool IsNetLogicFile { get; set; }
        public List<string> DominantCalls { get; set; } = new List<string>();
    }

    private class CodeFunctionUsage
    {
        public string Name { get; set; }
        public int Count { get; set; }
    }
}

// <SHERLOCK_ASSETS_BEGIN> generated by tools/sherlock-optix-widget/gen_sherlock_assets.ps1 - do not edit by hand
// Shared, embedded report assets for the Sherlock Optix diagnostics widgets.
// Embedded inside the ProjectDiagnostic NetLogic script so the assets travel
// with the widget when it is exported as a library. At run time the widgets
// emit these assets into a diagnostics folder OUTSIDE the project:
//   <two levels above ProjectFiles>/<ProjectName>_project_diagnostic
// NOTE: the const strings at the bottom are generated from the template files
// (report.css / report.js / report-template.html / logo_FTOptix_white.svg).
// Edit the source template and regenerate rather than hand-editing the strings.
public static class SherlockReportAssets
{
    public const string AssetsFolderName = "ProjectDiagnostics.assets";

    // <two levels above ProjectFiles>/<ProjectName>_project_diagnostic
    public static string GetDiagnosticOutputRoot()
    {
        var projectDir = Project.Current.ProjectDirectory;
        // ProjectDirectory may be "...\<Project>\ProjectFiles" or "...\<Project>"
        // depending on the runtime. Normalize to the folder that CONTAINS ProjectFiles.
        var dir = new DirectoryInfo(projectDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var projectRoot = string.Equals(dir.Name, "ProjectFiles", StringComparison.OrdinalIgnoreCase)
            ? dir.Parent                                            // ...\<Project>
            : dir;
        var projectName = projectRoot != null ? projectRoot.Name : "Project";
        var container = projectRoot != null && projectRoot.Parent != null
            ? projectRoot.Parent.FullName                           // ...\<container>
            : projectDir;
        var outputRoot = Path.Combine(container, projectName + "_project_diagnostic");
        Directory.CreateDirectory(outputRoot);
        return outputRoot;
    }

    // Writes css/js/logo into "<outputRoot>/ProjectDiagnostics.assets" and returns that dir.
    public static string EmitAssets(string outputRoot)
    {
        var assetsDir = Path.Combine(outputRoot, AssetsFolderName);
        Directory.CreateDirectory(assetsDir);
        File.WriteAllText(Path.Combine(assetsDir, "report.css"), ReportCss, Encoding.UTF8);
        File.WriteAllText(Path.Combine(assetsDir, "report.js"), ReportJs, Encoding.UTF8);
        File.WriteAllText(Path.Combine(assetsDir, "logo_FTOptix_white.svg"), LogoSvg, Encoding.UTF8);
        return assetsDir;
    }

    // Writes the HTML shell if it does not already exist (keeps it user-editable).
    public static void EnsureShell(string outputRoot, string htmlFileName)
    {
        var htmlPath = Path.Combine(outputRoot, htmlFileName);
        if (File.Exists(htmlPath))
            return;
        File.WriteAllText(htmlPath, ReportTemplateHtml, Encoding.UTF8);
    }
    public const string LogoSvg = @"<?xml version=""1.0"" encoding=""utf-8""?>
<svg version=""1.1"" id=""FT_Hub"" xmlns=""http://www.w3.org/2000/svg"" xmlns:xlink=""http://www.w3.org/1999/xlink"" x=""0px"" y=""0px"" viewBox=""0 0 303.6 170"" style=""enable-background:new 0 0 303.6 170;"" xml:space=""preserve"">
<style type=""text/css"">
	.st0{fill:#FFFFFF;}
</style>
<path class=""st0"" d=""M253.7,60.3c0,0-0.1-0.1-0.1-0.2v-0.7c0-0.1,0-0.1,0.1-0.2c0-0.1,0.1-0.1,0.2-0.1h5.1c0.1,0,0.1,0,0.2,0.1c0,0,0.1,0.1,0.1,0.2v0.7c0,0.1-0.1,0.2-0.2,0.2c0,0,0,0,0,0h-1.8c-0.1,0-0.1,0-0.1,0.1c0,0,0,0,0,0v5.8c0,0.1-0.1,0.2-0.2,0.2c0,0,0,0,0,0H256c-0.1,0-0.2-0.1-0.2-0.2c0,0,0,0,0,0v-5.8c0-0.1,0-0.1-0.1-0.1c0,0,0,0,0,0h-1.7C253.8,60.4,253.7,60.4,253.7,60.3z M265.3,59.2h0.9c0.1,0,0.1,0,0.2,0.1c0,0,0.1,0.1,0.1,0.2v6.9c0,0.1-0.1,0.2-0.2,0.2c0,0,0,0,0,0h-0.9c-0.1,0-0.1,0-0.2-0.1c-0.1,0-0.1-0.1-0.1-0.2v-4.8c0,0,0-0.1,0-0.1s0,0-0.1,0l-1.1,1.6c-0.1,0.1-0.2,0.2-0.3,0.2h-0.2c-0.1,0-0.2-0.1-0.3-0.2l-1.1-1.6c0,0,0,0-0.1,0s0,0,0,0.1v4.8c0,0.1,0,0.1-0.1,0.2c0,0-0.1,0.1-0.2,0.1h-0.9c-0.1,0-0.1,0-0.2-0.1c-0.1,0-0.1-0.1-0.1-0.2v-6.9c0-0.1,0-0.1,0.1-0.2c0-0.1,0.1-0.1,0.2-0.1h0.9c0.1,0,0.2,0.1,0.3,0.2l1.5,2.2c0,0,0.1,0,0.1,0c0,0,0.1,0,0.1,0l1.5-2.2C265.1,59.3,265.2,59.2,265.3,59.2z""/>
<path class=""st0"" d=""M115.6,65.8c0,0.5-0.4,0.9-0.9,0.9c0,0,0,0-0.1,0h-8.6c-0.2,0-0.3,0.1-0.4,0.3c0,0,0,0.1,0,0.1v27.3c0,0.2-0.1,0.5-0.3,0.7c-0.2,0.2-0.4,0.3-0.7,0.2h-6.7c-0.2,0-0.5-0.1-0.7-0.2c-0.2-0.2-0.3-0.4-0.3-0.7V67.1c0-0.2-0.1-0.3-0.3-0.4c0,0-0.1,0-0.1,0H75.4c-0.2,0-0.3,0.1-0.4,0.3c0,0,0,0,0,0v6.1c0,0.2,0.1,0.3,0.3,0.4c0,0,0.1,0,0.1,0h9.9c0.2,0,0.5,0.1,0.7,0.3c0.2,0.2,0.3,0.4,0.2,0.7v5.4c0,0.5-0.3,0.9-0.8,0.9c0,0-0.1,0-0.1,0h-9.9c-0.2,0-0.3,0.1-0.4,0.3c0,0,0,0.1,0,0.1v13.3c0,0.5-0.4,0.9-0.8,0.9c0,0-0.1,0-0.1,0h-6.7c-0.2,0-0.5-0.1-0.7-0.2c-0.2-0.2-0.3-0.4-0.3-0.7V60.3c0-0.2,0.1-0.5,0.3-0.7c0.2-0.2,0.4-0.3,0.7-0.2h47.3c0.2,0,0.5,0.1,0.7,0.2c0.2,0.2,0.3,0.4,0.3,0.7L115.6,65.8z M127.2,56.5L108.8,38c-2-1.9-4.6-3-7.3-3H76.2c-2.8,0-5.4,1.1-7.3,3L50.4,56.5c-1.6,1.6-2.5,3.7-2.5,5.9v26c0,2.8,1.1,5.4,3.1,7.4l17.8,17.8c2,1.9,4.6,3,7.3,3h13.1v14.8c0,0.3,0.3,0.6,0.6,0.6c0.2,0,0.3-0.1,0.4-0.2l36.8-36.7c1.6-1.6,2.5-3.7,2.5-5.9V62.4C129.6,60.2,128.7,58,127.2,56.5""/>
<path class=""st0"" d=""M38,95.8l18.4,18.4c1.6,1.6,3.7,2.5,5.9,2.5h3.2c-0.4-0.3-0.8-0.6-1.1-0.9L45.3,96.6c-2-2-3.2-4.8-3.2-7.6V62.7c0-2.9,1.1-5.6,3.2-7.6l19.1-19.2c0.3-0.3,0.7-0.6,1.1-0.9h-3.2c-2.2,0-4.4,0.9-5.9,2.5L38,55.9c-1.9,2-3,4.6-3,7.3v25.2C35,91.2,36.1,93.8,38,95.8z""/>
<g>
	<path class=""st0"" d=""M147.1,93.5c-2-1.1-3.5-2.6-4.6-4.5c-1.1-1.9-1.7-4.1-1.7-6.6V71.7c0-2.5,0.6-4.7,1.7-6.6c1.1-1.9,2.6-3.4,4.6-4.4c2-1,4.3-1.6,6.9-1.6c2.6,0,4.9,0.5,6.9,1.6s3.5,2.5,4.6,4.4c1.1,1.9,1.7,4.1,1.7,6.6v10.7c0,2.5-0.5,4.7-1.7,6.7c-1.1,1.9-2.6,3.4-4.6,4.5c-2,1.1-4.3,1.6-6.9,1.6C151.4,95.1,149.1,94.6,147.1,93.5z M158.4,87.3c1.1-1.2,1.7-2.7,1.7-4.7v-11c0-1.9-0.6-3.5-1.7-4.7c-1.1-1.2-2.6-1.8-4.5-1.8c-1.8,0-3.3,0.6-4.4,1.8c-1.1,1.2-1.7,2.7-1.7,4.7v11c0,1.9,0.6,3.5,1.7,4.7c1.1,1.2,2.6,1.8,4.4,1.8C155.8,89.1,157.3,88.5,158.4,87.3z""/>
	<path class=""st0"" d=""M195.1,81.8c0,2.3-0.3,4.4-1,6.2c-0.8,2.1-2,3.8-3.6,5.1s-3.7,1.9-6.1,1.9c-2.4,0-4.3-0.8-5.6-2.5c-0.1-0.1-0.1-0.1-0.2-0.1c-0.1,0-0.1,0.1-0.1,0.2v10.7c0,0.2-0.1,0.3-0.2,0.4c-0.1,0.1-0.3,0.2-0.4,0.2h-5.8c-0.2,0-0.3-0.1-0.4-0.2c-0.1-0.1-0.2-0.3-0.2-0.4V69.5c0-0.2,0.1-0.3,0.2-0.4c0.1-0.1,0.3-0.2,0.4-0.2h5.8c0.2,0,0.3,0.1,0.4,0.2c0.1,0.1,0.2,0.3,0.2,0.4v1.6c0,0.1,0,0.2,0.1,0.2c0.1,0,0.1,0,0.2-0.1c1.4-1.8,3.3-2.8,5.8-2.8c2.3,0,4.3,0.6,6,1.9c1.7,1.3,2.9,3.1,3.6,5.4C194.8,77.5,195.1,79.5,195.1,81.8z M187.8,81.7c0-2.1-0.4-3.8-1.1-5c-0.8-1.4-2-2.1-3.7-2.1c-1.5,0-2.7,0.7-3.5,2.1c-0.7,1.2-1,2.9-1,5.1c0,2.2,0.4,4,1.1,5.2c0.8,1.3,1.9,2,3.4,2s2.7-0.6,3.5-1.9C187.4,85.7,187.8,83.9,187.8,81.7z""/>
	<path class=""st0"" d=""M213.4,74.3c-0.1,0.1-0.3,0.2-0.4,0.2h-4.8c-0.2,0-0.2,0.1-0.2,0.2v10.6c0,1.1,0.2,1.9,0.7,2.5s1.2,0.8,2.2,0.8h1.7c0.2,0,0.3,0.1,0.4,0.2c0.1,0.1,0.2,0.3,0.2,0.4V94c0,0.4-0.2,0.6-0.6,0.6c-1.4,0.1-2.4,0.1-3,0.1c-2.8,0-4.8-0.5-6.2-1.4c-1.4-0.9-2.1-2.6-2.1-5.2V74.8c0-0.2-0.1-0.2-0.2-0.2H198c-0.2,0-0.3-0.1-0.4-0.2c-0.1-0.1-0.2-0.3-0.2-0.4v-4.4c0-0.2,0.1-0.3,0.2-0.4c0.1-0.1,0.3-0.2,0.4-0.2h2.9c0.2,0,0.2-0.1,0.2-0.2v-6c0-0.2,0.1-0.3,0.2-0.4c0.1-0.1,0.3-0.2,0.4-0.2h5.6c0.2,0,0.3,0.1,0.4,0.2c0.1,0.1,0.2,0.3,0.2,0.4v6c0,0.2,0.1,0.2,0.2,0.2h4.8c0.2,0,0.3,0.1,0.4,0.2c0.1,0.1,0.2,0.3,0.2,0.4v4.4C213.6,74.1,213.5,74.2,213.4,74.3z""/>
	<path class=""st0"" d=""M218,64.8c-0.8-0.8-1.1-1.7-1.1-2.8c0-1.2,0.4-2.1,1.1-2.9c0.7-0.7,1.7-1.1,2.9-1.1c1.2,0,2.1,0.4,2.8,1.1c0.7,0.7,1.1,1.7,1.1,2.9c0,1.1-0.4,2.1-1.1,2.8s-1.7,1.1-2.8,1.1C219.7,66,218.7,65.6,218,64.8z M217.4,94.4c-0.1-0.1-0.2-0.3-0.2-0.4V69.4c0-0.2,0.1-0.3,0.2-0.4c0.1-0.1,0.3-0.2,0.4-0.2h5.8c0.2,0,0.3,0.1,0.4,0.2c0.1,0.1,0.2,0.3,0.2,0.4V94c0,0.2-0.1,0.3-0.2,0.4c-0.1,0.1-0.3,0.2-0.4,0.2h-5.8C217.6,94.6,217.5,94.5,217.4,94.4z""/>
	<path class=""st0"" d=""M227.4,94.3c-0.1-0.2-0.1-0.3,0.1-0.5l8-12c0.1-0.1,0.1-0.2,0-0.3l-8-12c-0.1-0.1-0.1-0.2-0.1-0.3c0-0.3,0.2-0.4,0.5-0.4h6.2c0.3,0,0.5,0.1,0.7,0.4l4.5,6.8c0.1,0.2,0.2,0.2,0.4,0l4.5-6.8c0.2-0.3,0.4-0.4,0.7-0.4h6.7c0.2,0,0.4,0.1,0.5,0.2c0.1,0.1,0,0.3-0.1,0.5l-8,12c0,0.1,0,0.2,0,0.3l8,12c0.1,0.1,0.1,0.2,0.1,0.3c0,0.3-0.2,0.4-0.5,0.4h-6.3c-0.3,0-0.5-0.1-0.7-0.4l-4.5-6.8c-0.1-0.2-0.2-0.2-0.3,0l-4.5,6.8c-0.2,0.3-0.4,0.4-0.7,0.4h-6.7C227.7,94.6,227.5,94.5,227.4,94.3z""/>
</g>
</svg>
";

    public const string ReportTemplateHtml = @"<!DOCTYPE html>
<html lang=""en"">
<head>
  <meta charset=""UTF-8"">
  <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
  <title>FT Optix Project Analysis</title>
  <link rel=""stylesheet"" href=""ProjectDiagnostics.assets/report.css"">
</head>
<body>
  <div id=""app""></div>
  <script src=""ProjectDiagnostics.assets/report-data.js""></script>
  <script src=""ProjectDiagnostics.assets/report.js""></script>
</body>
</html>
";

    public const string ReportCss = @"* { margin: 0; padding: 0; box-sizing: border-box; }
:root {
  --bg-primary: #ffffff;
  --bg-secondary: #f3f5f7;
  --bg-tertiary: #f8fbfc;
  --text-primary: #1b2733;
  --text-secondary: #5a6875;
  --text-muted: #8b98a3;
  --border: #cbcfd2;
  --grid-head: #f1f4f6;
  --odd: #f8fbfc;
  --hover: rgba(35,93,159,0.08);
  --accent: #235d9f;
  --accent-light: #e9f0f8;
  --ok: #06804d;
  --warn: #e0a800;
  --err: #c0282d;
  --chart-track: #e3e7ea;
  --scrollbar-track: #e7ebf0;
  --scrollbar-thumb: #b9c3cf;
  --shadow-sm: 0 1px 3px rgba(0,0,0,0.10),0 1px 2px rgba(0,0,0,0.06);
  --shadow-md: 0 4px 12px rgba(0,0,0,0.10);
  --transition: all 0.25s cubic-bezier(0.4, 0, 0.2, 1);
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg-primary: #232a31;
    --bg-secondary: #171c21;
    --bg-tertiary: #1f262c;
    --text-primary: #e7ebee;
    --text-secondary: #9aa7b2;
    --text-muted: #8b98a3;
    --border: #3a444c;
    --grid-head: #2b333a;
    --odd: #1f262c;
    --hover: rgba(90,150,220,0.16);
    --accent: #5a9fe0;
    --accent-light: #24303d;
    --ok: #3fbf8a;
    --warn: #d7a742;
    --err: #e56b6f;
    --chart-track: #2b333a;
    --scrollbar-track: #1c2430;
    --scrollbar-thumb: #5e7188;
    --shadow-sm: 0 1px 3px rgba(0,0,0,0.45);
    --shadow-md: 0 8px 22px rgba(0,0,0,0.5);
  }
}
* {
  scrollbar-width: thin;
  scrollbar-color: var(--scrollbar-thumb) var(--scrollbar-track);
}
*::-webkit-scrollbar {
  width: 10px;
  height: 10px;
}
*::-webkit-scrollbar-track {
  background: var(--scrollbar-track);
}
*::-webkit-scrollbar-thumb {
  background: var(--scrollbar-thumb);
  border-radius: 999px;
  border: 2px solid var(--scrollbar-track);
}
*::-webkit-scrollbar-thumb:hover {
  filter: brightness(1.08);
}
body {
  font-family: Roboto, -apple-system, BlinkMacSystemFont, 'Segoe UI', Helvetica, Arial, sans-serif;
  background: var(--bg-secondary);
  color: var(--text-primary);
  line-height: 1.6;
}
.container {
  max-width: 1280px;
  margin: 0 auto;
  width: 100%;
}
header {
  background: #0f3f7f;
  border-bottom: 1px solid rgba(255, 255, 255, 0.22);
  position: sticky;
  top: 0;
  z-index: 100;
  box-shadow: var(--shadow-sm);
}
.header-content { max-width: 1280px; margin: 0 auto; padding: 10px 20px; }
.header-top { display: flex; justify-content: space-between; align-items: center; }
.header-left { display: flex; align-items: center; gap: 14px; }
.header-logo { width: 132px; height: auto; display: block; }
.header-logo-dark { display: none; }
.header-left h1 { font-size: 20px; font-weight: 600; margin-bottom: 1px; color: #ffffff; }
.header-left p { font-size: 13px; color: rgba(255, 255, 255, 0.88); }
.header-right { display: flex; gap: 8px; align-items: center; }
.badge { display: inline-flex; align-items: center; gap: 4px; padding: 4px 12px; border-radius: 999px; font-size: 12px; font-weight: 600; }
.badge-ok { background: rgba(52, 199, 89, 0.1); color: var(--ok); }
.badge-warn { background: rgba(255, 149, 0, 0.1); color: var(--warn); }
.badge-err { background: rgba(255, 59, 48, 0.1); color: var(--err); }
.content {
  padding: 16px 20px;
  height: auto;
  display: flex;
  flex-direction: column;
  gap: 12px;
  overflow: visible;
}
.nav-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 2px;
  margin: 10px 0 14px 0;
  border-bottom: 1px solid var(--border);
}
.nav-tab {
  border: 0;
  border-bottom: 2px solid transparent;
  border-radius: 0;
  background: transparent;
  color: var(--text-secondary);
  padding: 8px 14px;
  margin-bottom: -1px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: var(--transition);
}
.nav-tab:hover {
  color: var(--accent);
}
.nav-tab.active {
  border-bottom-color: var(--accent);
  background: transparent;
  color: var(--accent);
}
.tab-pane {
  display: none;
  animation: fadeIn 0.25s ease-in-out;
}
.tab-pane.active {
  display: block;
}
.model-tab {
  border: 0;
  border-bottom: 2px solid transparent;
  border-radius: 0;
  background: transparent;
  color: var(--text-secondary);
  padding: 8px 14px;
  margin-bottom: -1px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  transition: var(--transition);
}
.model-tab:hover {
  color: var(--accent);
}
.model-tab.active {
  border-bottom-color: var(--accent);
  background: transparent;
  color: var(--accent);
}
.model-pane {
  display: none;
  animation: fadeIn 0.25s ease-in-out;
}
.model-pane.active {
  display: block;
}
.section-block { animation: fadeIn 0.3s ease-in-out; margin-bottom: 24px; scroll-margin-top: 88px; }
.section-toggle {
  width: 100%;
  display: flex;
  justify-content: space-between;
  align-items: center;
  border: 1px solid var(--border);
  background: var(--bg-primary);
  color: var(--text-primary);
  border-radius: 12px;
  padding: 10px 14px;
  font-size: 14px;
  font-weight: 600;
  cursor: pointer;
  margin-bottom: 10px;
}
.section-chevron { transition: transform 0.2s ease-in-out; }
.section-body { display: none; }
.section-block.open .section-body { display: block; }
.section-block.open .section-chevron { transform: rotate(180deg); }
@keyframes fadeIn { from { opacity: 0; } to { opacity: 1; } }
.summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(140px, 1fr)); gap: 12px; margin-bottom: 24px; }
.summary-card { background: var(--bg-primary); border: 1px solid var(--border); border-radius: 12px; padding: 12px; text-align: center; transition: var(--transition); }
.summary-card:hover { border-color: var(--accent); box-shadow: var(--shadow-md); }
.summary-card-action {
  width: 100%;
  cursor: pointer;
  appearance: none;
  -webkit-appearance: none;
  font: inherit;
  color: inherit;
  background: var(--bg-primary);
}
.summary-card-action.active {
  border-color: var(--accent);
  box-shadow: 0 0 0 1px rgba(35, 93, 159, 0.22);
}
.summary-card .label { font-size: 12px; color: var(--text-secondary); margin-bottom: 4px; }
.summary-card .value { font-size: 28px; font-weight: 700; color: #235d9f; }
.summary-card .value-level {
  margin-top: 8px;
  display: inline-block;
  border-radius: 999px;
  padding: 3px 9px;
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}
.summary-card .value-level-low {
  background: rgba(52, 199, 89, 0.14);
  color: var(--ok);
}
.summary-card .value-level-medium {
  background: rgba(255, 149, 0, 0.16);
  color: var(--warn);
}
.summary-card .value-level-high {
  background: rgba(255, 59, 48, 0.14);
  color: var(--err);
}
.summary-card .value-group { display: flex; flex-direction: column; gap: 6px; width: max-content; margin: 8px auto 0 auto; }
.summary-card .value-item { display: grid; grid-template-columns: 32px minmax(40px, max-content); column-gap: 10px; align-items: center; }
.summary-card .value-item .k { font-size: 12px; color: var(--text-secondary); line-height: 1.2; text-align: left; min-width: 28px; }
.summary-card .value-item .v { font-size: 18px; font-weight: 700; color: #235d9f; line-height: 1.2; text-align: left; min-width: 40px; }
.alert-box { background: var(--accent-light); border-left: 4px solid var(--accent); border-radius: 8px; padding: 12px; margin-bottom: 16px; font-size: 14px; }
.alert-box.warn { background: rgba(255, 149, 0, 0.08); border-left-color: var(--warn); }
.alert-box.err { background: rgba(255, 59, 48, 0.08); border-left-color: var(--err); }
.alert-title { font-weight: 600; margin-bottom: 4px; }
.alert-text { color: var(--text-secondary); }
.chart-subtitle { font-size: 12px; color: var(--text-secondary); }
.chart-toggle { border: 1px solid var(--accent); background: var(--accent); color: #ffffff; border-radius: 4px; padding: 7px 14px; font-size: 13px; font-weight: 600; cursor: pointer; transition: var(--transition); }
.chart-toggle:hover { background: #0f3f7f; border-color: #0f3f7f; }
.item-list { background: var(--bg-primary); border: 1px solid var(--border); border-radius: 12px; overflow: hidden; }
.item-list-scroll { max-height: 56vh; overflow-y: auto; overflow-x: hidden; }
.item-row { border-bottom: 1px solid var(--border); transition: var(--transition); }
.item-row.sev-low { border-left: 1px solid var(--border); }
.item-row.sev-medium { border-left: 4px solid var(--warn); }
.item-row.sev-high { border-left: 4px solid var(--err); }
.item-row:last-child { border-bottom: none; }
.item-row:hover .item-head { background: var(--bg-tertiary); }
.item-row.expanded { background: var(--accent-light); }
.item-row-leaf .item-head { cursor: default; }
.item-row-leaf:hover .item-head { background: transparent; }
.item-head { padding: 12px 16px; display: flex; justify-content: space-between; align-items: center; gap: 10px; flex-wrap: wrap; cursor: pointer; }
.item-left { flex: 1; min-width: 0; }
.item-name { font-weight: 600; margin-bottom: 2px; font-size: 15px; }
.item-meta { font-size: 12px; color: var(--text-secondary); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.item-right { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; justify-content: flex-end; max-width: 100%; }
.metric-badge { background: var(--bg-tertiary); border: 1px solid var(--border); padding: 4px 8px; border-radius: 6px; min-width: 94px; display: flex; flex-direction: column; line-height: 1.15; }
.metric-label { font-size: 10px; color: var(--text-secondary); text-transform: uppercase; letter-spacing: 0.02em; }
.metric-value { font-size: 13px; font-weight: 700; color: var(--text-primary); }
.metric-badge-err .metric-label, .metric-badge-err .metric-value { color: var(--err); }
.expand-btn {
  width: 28px;
  height: 28px;
  border: none;
  background: transparent;
  color: var(--text-secondary);
  cursor: pointer;
  display: flex;
  align-items: center;
  justify-content: center;
  transition: var(--transition);
  font-size: 0;
}
.expand-btn::before {
  content: """";
  width: 0;
  height: 0;
  border-left: 6px solid transparent;
  border-right: 6px solid transparent;
  border-top: 8px solid currentColor;
  transition: transform 0.2s ease-in-out;
}
.expand-btn:hover { color: var(--accent); }
.expand-btn.open::before { transform: rotate(180deg); }
.item-details { max-height: 0; overflow: hidden; transition: max-height 0.3s ease-in-out; background: var(--bg-tertiary); border-top: 1px solid var(--border); }
.item-details.open { max-height: 12000px; }
.detail-content { padding: 14px 16px 16px 16px; display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; }
.detail-content-screens { display: flex; flex-direction: column; gap: 10px; }
.detail-section { background: var(--bg-primary); border: 1px solid var(--border); border-radius: 8px; padding: 12px; }
.detail-section-wide { width: 100%; }
.detail-section h4 { font-size: 12px; font-weight: 600; text-transform: uppercase; color: var(--text-secondary); margin-bottom: 8px; }
.detail-stack { display: flex; flex-direction: column; gap: 10px; }
.detail-list { list-style: none; }
.detail-list li { padding: 4px 0; font-size: 13px; color: var(--text-primary); }
.detail-list-flat li { display: grid; grid-template-columns: 1fr auto; gap: 12px; padding: 3px 0; border-bottom: 1px solid #f0f1f4; }
.detail-list-flat li:last-child { border-bottom: none; }
.detail-name { min-width: 0; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.detail-count { font-size: 12px; color: var(--text-secondary); white-space: nowrap; }
.top-target-list .target-extra { display: none; }
.detail-section.targets-expanded .top-target-list .target-extra { display: grid; }
.detail-kv { display: flex; flex-direction: column; gap: 6px; }
.detail-kv-compact { margin-bottom: 8px; }
.detail-kv-row { display: flex; justify-content: space-between; align-items: center; font-size: 13px; padding: 3px 0; border-bottom: 1px solid #f0f1f4; }
.detail-kv-row:last-child { border-bottom: none; }
.detail-kv-row span { color: var(--text-secondary); }
.detail-kv-row strong { color: var(--text-primary); font-size: 13px; }
.detail-subtitle { font-size: 11px; color: var(--text-secondary); text-transform: uppercase; letter-spacing: 0.02em; margin: 4px 0 6px 0; }
.detail-empty { font-size: 12px; color: var(--text-secondary); padding: 2px 0; }
.chart-toggle-inline { margin-top: 8px; }
.type-map-toolbar {
  display: grid;
  grid-template-columns: minmax(220px, 1fr) 180px 220px auto;
  gap: 10px;
  margin-bottom: 12px;
  align-items: center;
}
.screens-toolbar {
  display: grid;
  grid-template-columns: minmax(220px, 1fr) 220px 240px auto;
  gap: 10px;
  margin-bottom: 12px;
  align-items: center;
}
.dynamic-links-toolbar {
  display: grid;
  grid-template-columns: 1fr auto;
  gap: 10px;
  margin-bottom: 12px;
  align-items: center;
}
.type-map-input,
.type-map-select {
  width: 100%;
  height: 34px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-primary);
  color: var(--text-primary);
  padding: 0 10px;
  font-size: 13px;
}
.type-map-input:focus,
.type-map-select:focus {
  outline: none;
  border-color: var(--accent);
  box-shadow: 0 0 0 2px rgba(35, 93, 159, 0.12);
}
.type-map-breadcrumb { margin-bottom: 10px; }
.type-breadcrumb-pill {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  border: 1px solid var(--border);
  background: var(--bg-primary);
  border-radius: 999px;
  padding: 5px 10px;
  font-size: 12px;
}
.type-breadcrumb-label { color: var(--text-secondary); }
.type-breadcrumb-value { color: var(--text-primary); font-weight: 600; }
.type-breadcrumb-clear {
  border: none;
  background: transparent;
  color: var(--accent);
  cursor: pointer;
  font-size: 12px;
  font-weight: 600;
}
.type-map-stats { margin-bottom: 10px; }
.type-map-stats-pill {
  display: inline-block;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-primary);
  color: var(--text-secondary);
  padding: 6px 10px;
  font-size: 12px;
}
.severity-badge {
  display: inline-block;
  border-radius: 999px;
  padding: 4px 10px;
  font-size: 11px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.02em;
}
.severity-low { background: rgba(52, 199, 89, 0.12); color: var(--ok); }
.severity-medium { background: rgba(255, 149, 0, 0.15); color: var(--warn); }
.severity-high { background: rgba(255, 59, 48, 0.14); color: var(--err); }
.mini-badge {
  display: inline-block;
  border-radius: 999px;
  padding: 2px 8px;
  font-size: 10px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.02em;
  margin-right: 4px;
}
.mini-badge-complex { background: rgba(35, 93, 159, 0.14); color: var(--accent); }
.mini-badge-broken { background: rgba(255, 59, 48, 0.14); color: var(--err); }
.mini-badge-unresolved { background: rgba(255, 149, 0, 0.18); color: var(--warn); }
.mini-badge-repeat { background: rgba(52, 199, 89, 0.14); color: var(--ok); }
.dep-flat-list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
.dep-flat-list li { display: flex; align-items: center; gap: 10px; padding: 6px 10px; border-radius: 6px; background: var(--bg-tertiary); font-size: 13px; flex-wrap: wrap; }
.dep-flat-list li .detail-name { font-weight: 600; color: var(--text-primary); flex: 1 1 auto; min-width: 120px; }
.dep-stat { font-size: 12px; color: var(--text-secondary); white-space: nowrap; }
.dep-stat strong { color: var(--text-primary); }
.table-compact { width: 100%; border-collapse: collapse; background: var(--bg-primary); border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
.table-compact th { background: var(--grid-head); border-bottom: 1px solid var(--border); padding: 10px 14px; text-align: left; font-size: 14px; font-weight: 600; color: var(--text-primary); }
.table-compact td { padding: 9px 14px; border-bottom: 1px solid var(--border); font-size: 14px; }
.table-compact tbody tr:nth-child(odd) td { background: var(--odd); }
.table-compact tbody tr:hover td { background: var(--hover); }
.table-compact tr:last-child td { border-bottom: none; }
.table-scroll { overflow-x: auto; }
.table-scroll-dynlinks { max-height: 56vh; overflow-y: auto; overflow-x: auto; border: 1px solid var(--border); border-radius: 8px; background: var(--bg-primary); }
.table-scroll-dynlinks .table-compact { border: none; border-radius: 0; }
.table-scroll-entities { max-height: 44vh; overflow-y: auto; overflow-x: auto; border: 1px solid var(--border); border-radius: 8px; background: var(--bg-primary); }
.table-scroll-entities .table-compact { border: none; border-radius: 0; }
#commdrivers-host,
#commdrivers-host .detail-stack,
#commdrivers-host .detail-content,
#commdrivers-host .detail-section,
#commdrivers-host .item-list,
#commdrivers-host .item-list-scroll,
#commdrivers-host .commdrivers-tabpanel,
#commdrivers-tag-results { min-width: 0; max-width: 100%; }
#commdrivers-host .table-scroll { min-width: 0; width: 100%; max-width: 100%; overflow-x: auto; }
/* Requirement #9 fix: .project-category-stats .summary zeroes its own margin-bottom, which
   otherwise leaves no gap before the CommDrivers drill body. Scoped to CommDrivers only -- the
   shared .project-category-stats rule itself must stay untouched for other categories. */
#commdrivers-host { margin-top: 10px; }
.commdrivers-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  overflow-x: auto;
  padding-bottom: 2px;
  border-bottom: 1px solid var(--border);
  margin-bottom: 12px;
}
.commdrivers-tab {
  flex: 0 0 auto;
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 7px 12px;
  border: 1px solid var(--border);
  border-bottom: none;
  border-radius: 8px 8px 0 0;
  background: var(--bg-primary);
  color: var(--text-secondary);
  font: inherit;
  font-weight: 600;
  cursor: pointer;
  transition: var(--transition);
  max-width: 100%;
}
.commdrivers-tab:hover { color: var(--text-primary); border-color: var(--accent); }
.commdrivers-tab.active { color: var(--accent); border-color: var(--accent); background: var(--bg-secondary); }
.commdrivers-tab-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 20px;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--border);
  color: var(--text-secondary);
  font-size: 11px;
  font-weight: 700;
}
.commdrivers-tab.active .commdrivers-tab-count { background: var(--accent); color: #fff; }
.commdrivers-tag-toolbar { grid-template-columns: minmax(220px, 1fr) 220px; }
@media (max-width: 640px) {
  .commdrivers-tag-toolbar { grid-template-columns: 1fr; }
}
.mono { font-family: Menlo, Consolas, monospace; font-size: 12px; color: var(--text-secondary); }
.type-link { color: var(--accent); text-decoration: none; font-weight: 600; }
.type-link:hover { text-decoration: underline; }
.type-target-flash { animation: typeTargetFlash 1.6s ease; }
.usage-path-list li { padding: 5px 0; border-bottom: 1px solid #f0f1f4; }
.usage-path-list li:last-child { border-bottom: none; }
.usage-path { display: block; white-space: normal; overflow-wrap: anywhere; line-height: 1.35; }

.project-categories {
  margin-bottom: 18px;
}
.project-categories-grid {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.global-overview {
  margin-bottom: 22px;
}

@media (prefers-color-scheme: dark) {
  header {
    background: var(--bg-primary);
    border-bottom: 1px solid var(--border);
  }

  .header-logo-light { display: none; }
  .header-logo-dark { display: block; }
  .header-left h1 { color: var(--text-primary); }
  .header-left p { color: var(--text-secondary); }
}
.project-category-card {
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--bg-primary);
  padding: 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.project-category-card.sev-medium { border-left: 4px solid var(--warn); }
.project-category-card.sev-high { border-left: 4px solid var(--err); }
.project-category-preview { display: flex; flex-wrap: wrap; gap: 8px; cursor: pointer; }
.project-category-card.open .project-category-preview { display: none; }
.cat-preview-pill { display: inline-flex; align-items: center; gap: 8px; padding: 6px 12px; border: 1px solid var(--border); border-left: 3px solid var(--border); border-radius: 999px; background: var(--bg-primary); cursor: pointer; font: inherit; color: var(--text-primary); transition: var(--transition); }
.cat-preview-pill:hover { border-color: var(--accent); box-shadow: var(--shadow-sm); }
.cat-preview-pill .cpp-dot { width: 8px; height: 8px; border-radius: 50%; flex: 0 0 auto; }
.cat-preview-pill .cpp-k { font-size: 12px; color: var(--text-secondary); }
.cat-preview-pill .cpp-v { font-size: 14px; font-weight: 700; color: var(--accent); }
.cat-preview-pill.lvl-medium { border-left-color: var(--warn); }
.cat-preview-pill.lvl-medium .cpp-dot { background: var(--warn); }
.cat-preview-pill.lvl-high { border-left-color: var(--err); }
.cat-preview-pill.lvl-high .cpp-dot { background: var(--err); }
.project-category-stats { margin-top: 2px; }
.project-category-stats .summary { display: grid; grid-template-columns: repeat(auto-fit, minmax(170px, 1fr)); gap: 12px; margin-bottom: 0; align-items: stretch; }
.project-category-stats .summary-card { min-height: 82px; display: flex; flex-direction: column; justify-content: center; text-align: left; padding: 12px 14px; border-left: 3px solid var(--border); }
.project-category-stats .summary-card .label { text-align: left; margin-bottom: 4px; }
.project-category-stats .summary-card .value { text-align: left; }
.project-category-stats .summary-card.lvl-medium { border-left-color: var(--warn); }
.project-category-stats .summary-card.lvl-high { border-left-color: var(--err); }
.project-category-stats .summary-card.clickable { cursor: pointer; }
.project-category-stats .summary-card.clickable:hover { border-color: var(--accent); box-shadow: var(--shadow-md); }
.project-category-stats .summary-card:hover { border-left-color: var(--accent); }
.value-inline { font-size: 15px; font-weight: 700; color: var(--accent); }
.vi-k { color: var(--text-secondary); font-weight: 500; font-size: 12px; }
.vi-sep { color: var(--text-muted); margin: 0 4px; }
.findings { display: flex; flex-direction: column; gap: 8px; margin-bottom: 12px; }
.finding { border: 1px solid var(--border); border-left-width: 4px; border-radius: 8px; overflow: hidden; }
.finding--high { border-left-color: var(--err); }
.finding--medium { border-left-color: var(--warn); }
.finding--low { border-left-color: var(--warn); }
.finding-head { width: 100%; display: flex; align-items: center; gap: 10px; padding: 10px 12px; background: var(--bg-primary); border: 0; cursor: pointer; text-align: left; color: var(--text-primary); font: inherit; }
.finding-head:hover { background: var(--bg-tertiary); }
.finding-dot { width: 10px; height: 10px; border-radius: 50%; flex: 0 0 auto; }
.finding--high .finding-dot { background: var(--err); }
.finding--medium .finding-dot { background: var(--warn); }
.finding--low .finding-dot { background: var(--warn); }
.finding-title { font-weight: 600; font-size: 14px; white-space: nowrap; }
.finding-detail { color: var(--text-secondary); font-size: 12px; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.finding-chevron { color: var(--text-secondary); transition: transform 0.2s ease-in-out; flex: 0 0 auto; }
.finding.open .finding-chevron { transform: rotate(180deg); }
.finding-evidence { display: none; padding: 4px 12px 10px 12px; background: var(--bg-tertiary); }
.finding.open .finding-evidence { display: block; }
.evidence-row { display: flex; justify-content: space-between; gap: 12px; padding: 5px 0; border-bottom: 1px solid var(--border); font-size: 12px; }
.evidence-row:last-child { border-bottom: none; }
.evidence-target { color: var(--text-secondary); white-space: nowrap; }
.project-category-head {
  width: 100%;
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  border: 0;
  border-radius: 0;
  background: transparent;
  -webkit-appearance: none;
  appearance: none;
  outline: none;
  box-shadow: none;
  color: var(--text-primary);
  padding: 2px 0;
  cursor: pointer;
}
.project-category-head h4 {
  margin: 0;
  font-size: 14px;
  font-weight: 600;
}
.project-category-right { display: inline-flex; align-items: center; gap: 8px; }
.project-category-chevron {
  width: 28px;
  height: 28px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: var(--text-secondary);
  font-size: 0;
  transition: color 0.2s ease-in-out, transform 0.2s ease-in-out;
}
.project-category-chevron::before {
  content: """";
  width: 0;
  height: 0;
  border-left: 6px solid transparent;
  border-right: 6px solid transparent;
  border-top: 8px solid currentColor;
}
.project-category-head:hover h4,
.project-category-head:hover .project-category-chevron { color: var(--accent); }
.project-category-card.open .project-category-chevron { transform: rotate(180deg); }
.project-category-body { padding-top: 8px; }
.ui-accordion-tabs { margin-top: 8px; }
.category-actions {
  display: flex;
  gap: 8px;
  flex-wrap: wrap;
  margin-bottom: 10px;
}
.summary-inline { margin-bottom: 0; }
.summary-inline .summary-card { min-height: 92px; }
.category-lazy {
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--bg-tertiary);
  padding: 10px 12px;
  margin-top: 10px;
}
.global-overview {
  margin-bottom: 22px;
}
.section-title-block {
  margin-bottom: 14px;
}
.section-title-block h2 {
  font-size: 22px;
  font-weight: 700;
  margin-bottom: 2px;
}
.section-title-block p {
  font-size: 13px;
  color: var(--text-secondary);
}
.project-category-detail {
  border: 1px solid var(--border);
  border-radius: 10px;
  background: var(--bg-tertiary);
  padding: 10px 12px;
  margin-top: 8px;
}
.project-category-detail-text {
  font-size: 13px;
  color: var(--text-secondary);
  line-height: 1.45;
}
.project-category-detail-rich {
  display: flex;
  flex-direction: column;
  gap: 8px;
}
.project-category-detail-title {
  font-size: 12px;
  font-weight: 700;
  color: var(--text-secondary);
  text-transform: uppercase;
  letter-spacing: 0.04em;
}
.project-category-mini-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(150px, 1fr));
  gap: 8px;
}
.project-category-mini-item {
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--bg-primary);
  padding: 8px 10px;
  display: flex;
  flex-direction: column;
  gap: 2px;
}
.project-category-mini-item span {
  color: var(--text-secondary);
  font-size: 11px;
}
.project-category-mini-item strong {
  font-size: 14px;
  color: var(--text-primary);
}
.project-category-detail-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 6px;
}
.project-category-detail-list li {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  border-bottom: 1px solid var(--border);
  padding-bottom: 4px;
}
.project-category-detail-list li:last-child {
  border-bottom: 0;
  padding-bottom: 0;
}
.project-category-detail-list span {
  color: var(--text-primary);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.project-category-detail-list strong {
  color: var(--text-secondary);
  font-size: 12px;
  white-space: nowrap;
}

@media (max-width: 960px) {
  .content {
    height: auto;
    overflow: visible;
  }

  .type-map-toolbar { grid-template-columns: 1fr; }
  .screens-toolbar { grid-template-columns: 1fr; }
  .dynamic-links-toolbar { grid-template-columns: 1fr; }
}

@keyframes typeTargetFlash {
  0% { background: var(--accent-light); }
  100% { background: transparent; }
}
footer { padding: 20px; text-align: center; color: var(--text-secondary); font-size: 12px; border-top: 1px solid var(--border); margin-top: 40px; }
h3 { margin: 20px 0 12px 0; font-size: 16px; }
";

    public const string ReportJs = @"function escapeHtml(text) {
  if (text == null) return """";
  return String(text)
    .replace(/&/g, ""&amp;"")
    .replace(/</g, ""&lt;"")
    .replace(/>/g, ""&gt;"")
    .replace(/\""/g, ""&quot;"")
    .replace(/'/g, ""&#39;"");
}

function formatNumber(value) {
  const n = Number(value || 0);
  return Number.isFinite(n) ? n.toLocaleString(""en-US"") : ""0"";
}

function summaryCard(label, value, level) {
  const levelBadge = level
    ? ""<div class='value-level value-level-"" + escapeHtml(level) + ""'>"" + escapeHtml(level.toUpperCase()) + ""</div>""
    : """";

  return ""<div class='summary-card'>""
    + ""<div class='label'>"" + escapeHtml(label) + ""</div>""
    + ""<div class='value'>"" + escapeHtml(value) + ""</div>""
    + levelBadge
    + ""</div>"";
}

function parseMetricNumeric(value) {
  if (value == null) return null;
  const n = Number(value);
  if (Number.isFinite(n)) return n;

  const normalized = String(value)
    .replace(/,/g, '')
    .replace(/[^0-9.\-]/g, '');
  if (!normalized) return null;

  const parsed = Number(normalized);
  return Number.isFinite(parsed) ? parsed : null;
}

function buildMetricLevelResolver(rows) {
  const normalizedRows = Array.isArray(rows) ? rows : [];
  const values = normalizedRows
    .map(row => parseMetricNumeric(row && row.v))
    .filter(n => n != null)
    .sort((a, b) => a - b);

  if (values.length < 3) {
    return () => null;
  }

  const hasPercentValues = normalizedRows.some(row => /%\s*$/.test(String((row && row.v) || '')));
  if (hasPercentValues) {
    return metricValue => {
      const n = parseMetricNumeric(metricValue);
      if (n == null) return null;
      if (n <= 25) return 'low';
      if (n >= 70) return 'high';
      return 'medium';
    };
  }

  const min = values[0];
  const max = values[values.length - 1];
  const skewed = min > 0 && max / Math.max(min, 1) >= 100;
  const metricSpace = skewed
    ? values.map(v => Math.log10(v + 1))
    : values.slice();

  const idxLow = Math.floor((metricSpace.length - 1) * 0.33);
  const idxHigh = Math.floor((metricSpace.length - 1) * 0.66);
  const lowThreshold = metricSpace[idxLow];
  const highThreshold = metricSpace[idxHigh];

  return metricValue => {
    const n = parseMetricNumeric(metricValue);
    if (n == null) return null;
    const valueSpace = skewed ? Math.log10(n + 1) : n;
    if (valueSpace <= lowThreshold) return 'low';
    if (valueSpace >= highThreshold) return 'high';
    return 'medium';
  };
}

function summaryCardAction(label, value, onClickCode, isActive) {
  const action = onClickCode ? "" onclick=\"""" + onClickCode + ""\"""" : '';
  const cls = isActive ? ""summary-card summary-card-action active"" : ""summary-card summary-card-action"";
  return ""<button type='button' class='"" + cls + ""'"" + action + "">""
    + ""<div class='label'>"" + escapeHtml(label) + ""</div>""
    + ""<div class='value'>"" + escapeHtml(value) + ""</div>""
    + ""</button>"";
}

function toggleExpand(row) {
  const details = row.querySelector('.item-details');
  const btn = row.querySelector('.expand-btn');
  if (!details || !btn) return;

  const isOpen = details.classList.contains('open');
  details.classList.toggle('open', !isOpen);
  row.classList.toggle('expanded', !isOpen);
  btn.classList.toggle('open', !isOpen);
}

function abbreviatePathMiddle(path, maxLen) {
  const text = String(path || '');
  const limit = Number(maxLen || 120);
  if (text.length <= limit) return text;

  const keepLeft = Math.max(30, Math.floor((limit - 3) * 0.55));
  const keepRight = Math.max(20, limit - 3 - keepLeft);
  return text.slice(0, keepLeft) + '...' + text.slice(Math.max(0, text.length - keepRight));
}

const typeMapState = {
  allTypes: [],
  filteredTypes: [],
  query: '',
  kind: 'all',
  sort: 'name-asc',
  jumpFrom: ''
};

const screenMapState = {
  allScreens: [],
  filteredScreens: [],
  query: '',
  filter: 'all',
  sort: 'nodes-desc'
};

const dynamicLinksState = {
  targets: [],
  filteredTargets: [],
  query: ''
};

const reportRuntimeState = {
  payload: null,
  sectionCache: {},
  sectionLoading: {},
  sectionScriptLoading: {}
};

const modelState = {
  loaded: false,
  loading: false,
  data: null
};

const convertersState = {
  loaded: false,
  loading: false,
  data: null
};

const alarmsState = {
  loaded: false,
  loading: false,
  data: null
};

const recipesState = { loaded: false, loading: false, data: null };
const dataStoresState = { loaded: false, loading: false, data: null };
const loggersState = { loaded: false, loading: false, data: null };
const securityState = { loaded: false, loading: false, data: null };
const translationsState = { loaded: false, loading: false, data: null };
const reportsState = { loaded: false, loading: false, data: null };
const retentivityState = { loaded: false, loading: false, data: null };
const mqttState = { loaded: false, loading: false, data: null };
const netlogicState = { loaded: false, loading: false, data: null };
const opcuaState = { loaded: false, loading: false, data: null };
const commDriversState = { loaded: false, loading: false, data: null };

function getSectionScriptPath(payload, sectionKey, sectionPath) {
  if (payload && payload.sectionsScript && payload.sectionsScript[sectionKey])
    return String(payload.sectionsScript[sectionKey]);

  const pathText = String(sectionPath || '');
  if (!pathText)
    return '';

  return pathText.replace(/\.json(\?.*)?$/i, '.js$1');
}

function ensureSectionScriptLoaded(payload, sectionKey, sectionPath) {
  if (window.reportSectionData && window.reportSectionData[sectionKey])
    return Promise.resolve(window.reportSectionData[sectionKey]);

  if (reportRuntimeState.sectionScriptLoading[sectionKey])
    return reportRuntimeState.sectionScriptLoading[sectionKey];

  const scriptPath = getSectionScriptPath(payload, sectionKey, sectionPath);
  if (!scriptPath)
    return Promise.resolve(null);

  reportRuntimeState.sectionScriptLoading[sectionKey] = new Promise(resolve => {
    const existing = document.querySelector('script[data-section-key=""' + sectionKey + '""]');
    if (existing) {
      const data = window.reportSectionData && window.reportSectionData[sectionKey];
      resolve(data || null);
      return;
    }

    const script = document.createElement('script');
    script.src = scriptPath;
    script.async = true;
    script.setAttribute('data-section-key', sectionKey);
    script.onload = () => {
      const data = window.reportSectionData && window.reportSectionData[sectionKey];
      resolve(data || null);
    };
    script.onerror = () => resolve(null);
    document.head.appendChild(script);
  }).finally(() => {
    delete reportRuntimeState.sectionScriptLoading[sectionKey];
  });

  return reportRuntimeState.sectionScriptLoading[sectionKey];
}

const preferredProjectCategoryOrder = [
  'ui', 'model', 'converters', 'alarms', 'recipes', 'datastores', 'loggers', 'reports', 'mqtt',
  'opcua', 'commdrivers', 'netlogic', 'security', 'system', 'translations', 'retentivity'
];

const knownProjectCategoryLabels = {
  ui: 'UI',
  model: 'Model',
  converters: 'Converters',
  alarms: 'Alarms',
  recipes: 'Recipes',
  datastores: 'DataStores',
  loggers: 'Loggers',
  reports: 'Reports',
  mqtt: 'MQTT',
  opcua: 'OPC-UA',
  commdrivers: 'CommDrivers',
  netlogic: 'NetLogic',
  security: 'Security',
  system: 'System',
  translations: 'Translations',
  retentivity: 'Retentivity'
};

function formatCategoryLabel(categoryKey) {
  const raw = String(categoryKey || '').trim();
  if (!raw) return 'Unknown';
  const words = raw.replace(/[_-]+/g, ' ').split(/\s+/).filter(Boolean);
  return words.map(word => word.charAt(0).toUpperCase() + word.slice(1)).join(' ');
}

function getCategoryLabel(categoryKey) {
  const key = String(categoryKey || '').toLowerCase();
  return knownProjectCategoryLabels[key] || formatCategoryLabel(key);
}

function getProjectCategoryDefs(data) {
  const categoryMetrics = (data && data.categoryMetrics && typeof data.categoryMetrics === 'object') ? data.categoryMetrics : {};
  const incomingKeys = Object.keys(categoryMetrics)
    .map(key => String(key || '').trim())
    .filter(Boolean);

  const preferred = preferredProjectCategoryOrder.filter(key => incomingKeys.indexOf(key) >= 0);
  const extras = incomingKeys
    .filter(key => preferred.indexOf(key) < 0)
    .sort((a, b) => a.localeCompare(b));
  const ordered = preferred.concat(extras);

  return ordered.map(key => ({ key, label: getCategoryLabel(key) }));
}

function buildUiNodesDetailHtml(overview) {
  return ""<div class='project-category-detail-rich'>""
    + ""<div class='project-category-detail-title'>Node Overview</div>""
    + ""<div class='project-category-mini-grid'>""
    + ""<div class='project-category-mini-item'><span>Total Nodes</span><strong>"" + formatNumber(overview.uiTotalNodes) + ""</strong></div>""
    + ""<div class='project-category-mini-item'><span>UI Objects</span><strong>"" + formatNumber(overview.uiObjectNodes) + ""</strong></div>""
    + ""<div class='project-category-mini-item'><span>Max Depth</span><strong>"" + formatNumber(overview.maxDepth) + ""</strong></div>""
    + ""<div class='project-category-mini-item'><span>Dynamic Links</span><strong>"" + formatNumber(overview.totalDynamicLinks) + ""</strong></div>""
    + ""</div>""
    + ""</div>"";
}

function buildUiScreensDetailHtml(screens) {
  const rows = (Array.isArray(screens) ? screens : [])
    .slice()
    .sort((a, b) => Number(b.nodeCount || 0) - Number(a.nodeCount || 0))
    .slice(0, 8);

  if (!rows.length) {
    return ""<div class='project-category-detail-rich'><div class='project-category-detail-title'>Screens</div><div class='project-category-detail-text'>No screen data available.</div></div>"";
  }

  const list = rows.map(item =>
    ""<li><span>"" + escapeHtml(item.name || 'Unnamed') + ""</span><strong>"" + formatNumber(item.nodeCount) + "" nodes</strong></li>""
  ).join('');

  return ""<div class='project-category-detail-rich'>""
    + ""<div class='project-category-detail-title'>Top Screens by Node Count</div>""
    + ""<ul class='project-category-detail-list'>"" + list + ""</ul>""
    + ""</div>"";
}

function buildUiTypeMapDetailHtml(types) {
  const rows = (Array.isArray(types) ? types : [])
    .slice()
    .sort((a, b) => Number(b.usageCount || 0) - Number(a.usageCount || 0))
    .slice(0, 8);

  if (!rows.length) {
    return ""<div class='project-category-detail-rich'><div class='project-category-detail-title'>Type Map</div><div class='project-category-detail-text'>No type data available.</div></div>"";
  }

  const list = rows.map(item =>
    ""<li><span>"" + escapeHtml(item.name || 'Unnamed type') + ""</span><strong>"" + formatNumber(item.usageCount) + "" usage</strong></li>""
  ).join('');

  return ""<div class='project-category-detail-rich'>""
    + ""<div class='project-category-detail-title'>Top Types by Usage</div>""
    + ""<ul class='project-category-detail-list'>"" + list + ""</ul>""
    + ""</div>"";
}

function buildProjectCategoryStats(data) {
  const overview = data.overview || {};
  const summary = data.summary || {};
  const categoryMetrics = data.categoryMetrics || {};
  const categoryDefs = getProjectCategoryDefs(data);
  const crossCutting = data.crossCutting || {};
  const specialMetrics = data.specialMetrics || {};
  const commDriversMetrics = specialMetrics.commdrivers || {};
  const opcUaMetrics = specialMetrics.opcua || {};
  const convertersMetrics = specialMetrics.converters || {};
  const alarmsMetrics = specialMetrics.alarms || {};
  const recipesMetrics = specialMetrics.recipes || {};
  const datastoresMetrics = specialMetrics.datastores || {};
  const loggersMetrics = specialMetrics.loggers || {};
  const reportsMetrics = specialMetrics.reports || {};
  const securityMetrics = specialMetrics.security || {};
  const translationsMetrics = specialMetrics.translations || {};
  const retentivityMetrics = specialMetrics.retentivity || {};
  const types = Array.isArray(data.types) ? data.types : [];
  const screens = Array.isArray(data.screens) ? data.screens : [];
  const dynamicLinks = data.dynamicLinks || {};
  const loadedUsagePaths = types.reduce((sum, item) => sum + Number(item.usageCount || 0), 0);
  const totalUsagePaths = loadedUsagePaths > 0 ? loadedUsagePaths : Number(summary.totalUsagePaths || 0);
  const typesCount = types.length > 0 ? types.length : Number(summary.typesCount || overview.typesCount || 0);
  const dynamicLinksComplex = typeof dynamicLinks.complex !== 'undefined'
    ? Number(dynamicLinks.complex || 0)
    : Number(summary.dynamicLinksComplex || 0);
  const complexRatio = Number(overview.complexRatio || 0).toFixed(1) + '%';
  const hasDynamicLinksCollector = typeof dynamicLinks.complex !== 'undefined' || typeof summary.dynamicLinksComplex !== 'undefined';

  function genericCategoryRowsFor(key, fallbackNodes) {
    const cat = categoryMetrics[key] || {};
    const nodes = typeof cat.nodes !== 'undefined' ? Number(cat.nodes || 0) : Number(fallbackNodes || 0);
    const folders = typeof cat.folders !== 'undefined' ? Number(cat.folders || 0) : 0;
    const objects = typeof cat.objects !== 'undefined' ? Number(cat.objects || 0) : 0;
    const variables = typeof cat.variables !== 'undefined' ? Number(cat.variables || 0) : 0;
    const links = typeof cat.dynamicLinks !== 'undefined' ? Number(cat.dynamicLinks || 0) : 0;
    const netLogic = typeof cat.netLogic !== 'undefined' ? Number(cat.netLogic || 0) : 0;

    return [
      { k: 'Nodes', v: formatNumber(nodes), detail: 'Nodes detected in this project-tree category.' },
      { k: 'Folders', v: formatNumber(folders), detail: 'Folder-like nodes detected in this category.' },
      { k: 'Objects', v: formatNumber(objects), detail: 'Object-like nodes detected in this category.' },
      { k: 'Variables', v: formatNumber(variables), detail: 'Variable-like nodes detected in this category.' },
      { k: 'Dynamic Links', v: formatNumber(links), detail: 'Dynamic links whose source node belongs to this category.' },
      { k: 'NetLogic', v: formatNumber(netLogic), detail: 'NetLogic nodes whose owner path belongs to this category.' }
    ];
  }

  const stats = {
    ui: {
      measured: true,
      rows: [
        { k: 'Total Nodes', v: formatNumber(overview.uiTotalNodes), detail: 'Total project nodes parsed in the UI analysis scope.' },
        { k: 'UI Objects', v: formatNumber(overview.uiObjectNodes), detail: 'Count of UI object nodes found during traversal.' },
        { k: 'Types', v: formatNumber(typesCount), detail: 'Unique custom types tracked in the Type Map.' },
        { k: 'Screens', v: formatNumber(overview.screensCount), detail: 'Number of screens detected in the UI category.' },
        { k: 'Dynamic Links', v: formatNumber(overview.totalDynamicLinks), detail: 'Project-wide dynamic links detected during scan.' },
        { k: 'Max Depth', v: formatNumber(overview.maxDepth), detail: 'Maximum traversal depth reached in the UI tree.' }
      ]
    },
    model: {
      measured: true,
      rows: [
        { k: 'Types', v: formatNumber(typesCount), detail: 'Unique custom types tracked in the Type Map.' },
        { k: 'Usage Paths', v: formatNumber(totalUsagePaths), detail: 'Total usage paths where tracked types are referenced.' },
        ...genericCategoryRowsFor('model', 0)
      ]
    },
    converters: {
      measured: hasDynamicLinksCollector,
      rows: [
        { k: 'Converters', v: formatNumber(Number(convertersMetrics.converterNodes || 0)), detail: 'Nodes whose runtime type is converter-related.' },
        { k: 'Complex Links', v: formatNumber(dynamicLinksComplex), detail: 'Dynamic links using converters in their definition.' },
        { k: 'Complex Ratio', v: complexRatio, detail: 'Percentage of converter-based links vs total links.' },
        ...genericCategoryRowsFor('converters', 0)
      ]
    },
    alarms: {
      measured: true,
      rows: [
        { k: 'Alarm Nodes', v: formatNumber(Number(alarmsMetrics.alarmNodes || 0)), detail: 'Nodes whose runtime type is alarm-related.' },
        ...genericCategoryRowsFor('alarms', 0)
      ]
    },
    recipes: {
      measured: true,
      rows: [
        { k: 'Recipe Nodes', v: formatNumber(Number(recipesMetrics.recipeNodes || 0)), detail: 'Nodes whose runtime type is recipe-related.' },
        ...genericCategoryRowsFor('recipes', 0)
      ]
    },
    datastores: {
      measured: true,
      rows: [
        { k: 'Store Nodes', v: formatNumber(Number(datastoresMetrics.storeNodes || 0)), detail: 'Nodes whose runtime type is store-related.' },
        ...genericCategoryRowsFor('datastores', 0)
      ]
    },
    loggers: {
      measured: true,
      rows: [
        { k: 'Logger Nodes', v: formatNumber(Number(loggersMetrics.loggerNodes || 0)), detail: 'Nodes whose runtime type is logger-related.' },
        ...genericCategoryRowsFor('loggers', 0)
      ]
    },
    reports: {
      measured: true,
      rows: [
        { k: 'Report Nodes', v: formatNumber(Number(reportsMetrics.reportNodes || 0)), detail: 'Nodes whose runtime type is report-related.' },
        ...genericCategoryRowsFor('reports', 0)
      ]
    },
    opcua: {
      measured: true,
      rows: [
        { k: 'Clients', v: formatNumber(Number(opcUaMetrics.clients || 0)), detail: 'OPC-UA client nodes detected project-wide.' },
        { k: 'Servers', v: formatNumber(Number(opcUaMetrics.servers || 0)), detail: 'OPC-UA server nodes detected project-wide.' },
        { k: 'Subscriptions', v: formatNumber(Number(opcUaMetrics.subscriptions || 0)), detail: 'OPC-UA subscription nodes detected project-wide.' },
        ...genericCategoryRowsFor('opcua', 0)
      ]
    },
    commdrivers: {
      measured: true,
      rows: [
        { k: 'Drivers', v: formatNumber(Number(commDriversMetrics.drivers || 0)), detail: 'Communication drivers detected project-wide.' },
        { k: 'Tags', v: formatNumber(Number(commDriversMetrics.tags || 0)), detail: 'Driver tags detected project-wide.' },
        { k: 'Tags With Incoming Dynamic-Link Ref', v: formatNumber(Number(commDriversMetrics.tagsWithIncomingRef || 0)), detail: 'Tags with at least one resolved incoming DynamicLink reference.' },
        { k: 'Tags Without Incoming Dynamic-Link Ref', v: formatNumber(Number(commDriversMetrics.tagsWithoutIncomingRef || 0)), detail: 'Tags with no resolved incoming DynamicLink reference detected. Not proof the tag is unused -- see CommDrivers limitation note.' },
        { k: 'Resolved Incoming Dynamic-Link Refs', v: formatNumber(Number(commDriversMetrics.resolvedIncomingRefCount || 0)), detail: 'Total resolved DynamicLink references to CommDriver tags, project-wide.' },
        ...genericCategoryRowsFor('commdrivers', 0)
      ]
    },
    netlogic: {
      measured: true,
      rows: [
        { k: 'Global NetLogic', v: formatNumber(Number(crossCutting.netLogicTotal || 0)), detail: 'Total NetLogic nodes found project-wide.' },
        ...genericCategoryRowsFor('netlogic', 0)
      ]
    },
    security: {
      measured: true,
      rows: [
        { k: 'Users', v: formatNumber(Number(securityMetrics.users || 0)), detail: 'Nodes whose runtime type is user-related.' },
        { k: 'Groups', v: formatNumber(Number(securityMetrics.groups || 0)), detail: 'Nodes whose runtime type is group-related.' },
        { k: 'Roles', v: formatNumber(Number(securityMetrics.roles || 0)), detail: 'Nodes whose runtime type is role-related.' },
        ...genericCategoryRowsFor('security', 0)
      ]
    },
    system: { measured: true, rows: genericCategoryRowsFor('system', 0) },
    translations: {
      measured: true,
      rows: [
        { k: 'Translation Nodes', v: formatNumber(Number(translationsMetrics.translationNodes || 0)), detail: 'Nodes whose runtime type is translation-related.' },
        { k: 'Locales', v: formatNumber(Number(translationsMetrics.locales || 0)), detail: 'Nodes whose runtime type is locale-related.' },
        ...genericCategoryRowsFor('translations', 0)
      ]
    },
    retentivity: {
      measured: true,
      rows: [
        { k: 'Retentive Nodes', v: formatNumber(Number(retentivityMetrics.retentivityNodes || 0)), detail: 'Nodes whose runtime type is retentivity-related.' },
        ...genericCategoryRowsFor('retentivity', 0)
      ]
    }
  };

  categoryDefs.forEach(def => {
    if (!stats[def.key]) {
      stats[def.key] = {
        measured: true,
        rows: genericCategoryRowsFor(def.key, 0)
      };
    }
  });

  Object.keys(stats).forEach(key => {
    stats[key].attention = computeCategoryAttention(key, data);
  });

  return stats;
}

function computeCategoryAttention(categoryKey, data) {
  const categoryMetrics = data.categoryMetrics || {};
  const overview = data.overview || {};
  const cat = categoryMetrics[categoryKey] || {};
  const nodes = Number(cat.nodes || 0);
  const dynamicLinks = Number(cat.dynamicLinks || 0);
  const netLogic = Number(cat.netLogic || 0);
  const special = data.specialMetrics || {};
  let score = 0;

  if (nodes > 0) {
    const linkDensity = (dynamicLinks * 100) / Math.max(nodes, 1);
    if (linkDensity >= 20) score += 2;
    else if (linkDensity >= 5) score += 1;

    const logicDensity = (netLogic * 100) / Math.max(nodes, 1);
    if (logicDensity >= 5) score += 2;
    else if (logicDensity >= 1) score += 1;
  }

  if (categoryKey === 'ui') {
    if (Number(overview.totalBrokenDynamicLinks || 0) > 0) score += 3;
    if (Number(overview.complexRatio || 0) >= 30) score += 2;
  }

  if (categoryKey === 'security' && nodes > 0) {
    const sec = special.security || {};
    const secSignals = Number(sec.users || 0) + Number(sec.groups || 0) + Number(sec.roles || 0);
    if (secSignals === 0) score += 2;
  }

  if (categoryKey === 'translations' && nodes > 0) {
    const tr = special.translations || {};
    if (Number(tr.locales || 0) === 0) score += 2;
  }

  if (categoryKey === 'retentivity' && nodes > 0) {
    const ret = special.retentivity || {};
    if (Number(ret.retentivityNodes || 0) === 0) score += 2;
  }

  let level = 'low';
  if (score >= 5) level = 'high';
  else if (score >= 3) level = 'medium';

  return { score, level };
}

function renderAttentionBadge(attention) {
  const level = attention && attention.level ? attention.level : 'low';
  const label = level === 'high' ? 'High' : (level === 'medium' ? 'Medium' : 'Low');
  const className = level === 'high' ? 'severity-high' : (level === 'medium' ? 'severity-medium' : 'severity-low');
  return ""<span class='severity-badge "" + className + ""'>Attention "" + label + ""</span>"";
}

function buildCategoryMetricsCsv(categoryKey, payload) {
  const stats = buildProjectCategoryStats(payload);
  const info = stats[categoryKey] || { rows: [], attention: { level: 'low', score: 0 } };
  const rows = Array.isArray(info.rows) ? info.rows : [];

  function csvEscape(value) {
    const text = String(value == null ? '' : value);
    if (/["",\n\r]/.test(text)) return '""' + text.replace(/""/g, '""""') + '""';
    return text;
  }

  const lines = ['category,metric,value'];
  rows.forEach(r => lines.push([categoryKey, r.k, r.v].map(csvEscape).join(',')));
  lines.push([categoryKey, 'attention_level', info.attention.level].map(csvEscape).join(','));
  lines.push([categoryKey, 'attention_score', info.attention.score].map(csvEscape).join(','));
  return lines.join('\r\n');
}

function downloadCategoryMetricsCsv(categoryKey) {
  const payload = reportRuntimeState.payload;
  if (!payload) return;
  const csv = buildCategoryMetricsCsv(categoryKey, payload);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = categoryKey + '-metrics.csv';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function buildCrossCuttingCsv(payload) {
  const cross = payload.crossCutting || {};
  const solution = payload.solutionInsights || {};
  const lines = ['group,category,value'];
  lines.push('global,dynamicLinksTotal,' + Number(cross.dynamicLinksTotal || 0));
  lines.push('global,netLogicTotal,' + Number(cross.netLogicTotal || 0));
  lines.push('global,netLogicDesign,' + Number(cross.netLogicDesign || 0));
  lines.push('global,netLogicRuntime,' + Number(cross.netLogicRuntime || 0));
  lines.push('solution,csFiles,' + Number(solution.csFiles || 0));
  lines.push('solution,netLogicFiles,' + Number(solution.netLogicFiles || 0));
  lines.push('solution,methods,' + Number(solution.totalMethods || 0));
  lines.push('solution,complexity,' + Number(solution.totalComplexity || 0));
  (Array.isArray(cross.dynamicLinksByCategory) ? cross.dynamicLinksByCategory : []).forEach(item => {
    lines.push('dynamicLinksByCategory,' + String(item.category || '') + ',' + Number(item.count || 0));
  });
  (Array.isArray(cross.netLogicByCategory) ? cross.netLogicByCategory : []).forEach(item => {
    lines.push('netLogicByCategory,' + String(item.category || '') + ',' + Number(item.count || 0));
  });
  return lines.join('\r\n');
}

function downloadCrossCuttingCsv() {
  const payload = reportRuntimeState.payload;
  if (!payload) return;
  const csv = buildCrossCuttingCsv(payload);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = 'cross-cutting.csv';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function renderCrossCuttingBreakdown(items, title) {
  const rows = (Array.isArray(items) ? items : [])
    .slice()
    .sort((a, b) => Number(b.count || 0) - Number(a.count || 0))
    .slice(0, 8);

  if (!rows.length) {
    return ""<div class='detail-section'><h4>"" + escapeHtml(title) + ""</h4><div class='detail-empty'>No data available.</div></div>"";
  }

  return ""<div class='detail-section'><h4>"" + escapeHtml(title) + ""</h4><ul class='project-category-detail-list'>""
    + rows.map(item => ""<li><span>"" + escapeHtml(String(item.category || '').toUpperCase()) + ""</span><strong>"" + formatNumber(item.count) + ""</strong></li>"").join('')
    + ""</ul></div>"";
}

function buildCrossCuttingTabHtml(payload, section) {
  const src = section || payload || {};
  const cross = src.crossCutting || (payload && payload.crossCutting) || {};
  const netlogic = src.netLogicDetails || (payload && payload.categoryDetails && payload.categoryDetails.netlogic) || {};
  const solution = src.solutionInsights || (payload && payload.solutionInsights) || {};
  const dynByCategory = Array.isArray(cross.dynamicLinksByCategory) ? cross.dynamicLinksByCategory : [];
  const netByCategory = Array.isArray(cross.netLogicByCategory) ? cross.netLogicByCategory : [];
  const topFiles = Array.isArray(solution.topFiles) ? solution.topFiles : [];
  const topFunctions = Array.isArray(solution.topFunctions) ? solution.topFunctions : [];
  const netTypeRows = Array.isArray(netlogic.typeDistribution) ? netlogic.typeDistribution : [];
  const netlogicSummaryRows = [
    { k: 'NetLogic Design', v: Number(cross.netLogicDesign || 0) },
    { k: 'NetLogic Runtime', v: Number(cross.netLogicRuntime || 0) },
    { k: 'C# Files', v: Number(solution.csFiles || 0) },
    { k: 'NetLogic C# Files', v: Number(solution.netLogicFiles || 0) },
    { k: 'Methods', v: Number(solution.totalMethods || 0) },
    { k: 'Estimated Complexity', v: Number(solution.totalComplexity || 0) }
  ];

  return ""<div class='category-actions'><button class='chart-toggle' type='button' onclick='downloadCrossCuttingCsv()'>Export Cross-Cutting CSV</button></div>""
    + ""<h3>Global Signals</h3>""
    + ""<div class='detail-section'><div class='table-scroll'><table class='table-compact'><thead><tr><th>Metric</th><th>Value</th></tr></thead><tbody>""
    + netlogicSummaryRows.map(row => ""<tr><td>"" + escapeHtml(row.k) + ""</td><td>"" + formatNumber(row.v) + ""</td></tr>"").join('')
    + ""</tbody></table></div></div>""
    + ""<h3>Distribution by Category</h3>""
    + ""<div class='project-category-mini-grid'>""
    + ""<div class='detail-section'><h4>Dynamic Links by Category</h4>""
    + (dynByCategory.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Category</th><th>Count</th></tr></thead><tbody>"" + dynByCategory.slice().sort((a, b) => Number(b.count || 0) - Number(a.count || 0)).map(item => ""<tr><td>"" + escapeHtml(String(item.category || '').toUpperCase()) + ""</td><td>"" + formatNumber(item.count || 0) + ""</td></tr>"").join('') + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No data available.</div>"")
    + ""</div>""
    + ""<div class='detail-section'><h4>NetLogic by Category</h4>""
    + (netByCategory.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Category</th><th>Count</th></tr></thead><tbody>"" + netByCategory.slice().sort((a, b) => Number(b.count || 0) - Number(a.count || 0)).map(item => ""<tr><td>"" + escapeHtml(String(item.category || '').toUpperCase()) + ""</td><td>"" + formatNumber(item.count || 0) + ""</td></tr>"").join('') + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No data available.</div>"")
    + ""</div>""
    + ""</div>""
    + ""<h3>NetLogic Repeated Types</h3>""
    + ""<div class='detail-section'>""
    + (netTypeRows.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>NetLogic</th><th>Count</th></tr></thead><tbody>"" + netTypeRows.slice(0, 30).map(item => ""<tr><td>"" + escapeHtml(item.name || '-') + ""</td><td>"" + formatNumber(item.count || 0) + ""</td></tr>"").join('') + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No NetLogic type distribution available.</div>"")
    + ""</div>""
    + ""<h3>Solution Hotspots</h3>""
    + ""<div class='project-category-mini-grid'>""
    + ""<div class='detail-section'><h4>Top Impact Files (.cs)</h4>""
    + (topFiles.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>File</th><th>Impact</th><th>Methods</th><th>Complexity</th></tr></thead><tbody>"" + topFiles.slice(0, 20).map(item => ""<tr><td class='mono' title='"" + escapeHtml(item.path || '') + ""'>"" + escapeHtml(item.path || '-') + ""</td><td>"" + formatNumber(item.impact || 0) + ""</td><td>"" + formatNumber(item.methods || 0) + ""</td><td>"" + formatNumber(item.complexity || 0) + ""</td></tr>"").join('') + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No file insights available.</div>"")
    + ""</div>""
    + ""<div class='detail-section'><h4>Most Used Function Calls</h4>""
    + (topFunctions.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Function</th><th>Calls</th></tr></thead><tbody>"" + topFunctions.slice(0, 25).map(item => ""<tr><td>"" + escapeHtml(item.name || '-') + ""</td><td>"" + formatNumber(item.count || 0) + ""</td></tr>"").join('') + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No function insights available.</div>"")
    + ""</div>""
    + ""</div>"";
}

function renderCategoryLazyHost(categoryKey) {
  return ""<div class='category-lazy' id='category-lazy-"" + escapeHtml(categoryKey) + ""'><div class='detail-empty'>Open details to load data...</div></div>"";
}

function renderConvertersLazy(section) {
  const s = section || {};
  const items = Array.isArray(s.items) ? s.items : [];
  if (!items.length) return ""<div class='detail-empty'>No shared converters defined under the Converters folder.</div>"";

  const cards = items.map(c => {
    const usagePaths = Array.isArray(c.usagePaths) ? c.usagePaths : [];
    const usageSection = usagePaths.length
      ? ""<ul class='detail-list'>"" + usagePaths.map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 130)) + ""</li>"").join('') + ""</ul>""
        + (Number(c.usageCount) > usagePaths.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(usagePaths.length) + "" of "" + formatNumber(c.usageCount) + "" usages.</div>"" : """")
      : ""<div class='detail-empty'>Not used by any dynamic link.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(c.name) + ""</div><div class='item-meta' title='"" + escapeHtml(c.path || '') + ""'>"" + escapeHtml(c.type || 'Converter') + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Used by</span><span class='metric-value'>"" + formatNumber(c.usageCount) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Used in ("" + formatNumber(c.usageCount) + "")</h4>"" + usageSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Converters ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function renderConvertersOtherHtml(section) {
  const s = section || {};
  const items = Array.isArray(s.otherItems) ? s.otherItems : [];
  if (!items.length) return ""<div class='detail-empty'>No other type definitions found under the Converters folder.</div>"";

  const cards = items.map(c => {
    const usagePaths = Array.isArray(c.usagePaths) ? c.usagePaths : [];
    const usageSection = usagePaths.length
      ? ""<ul class='detail-list'>"" + usagePaths.map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 130)) + ""</li>"").join('') + ""</ul>""
        + (Number(c.usageCount) > usagePaths.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(usagePaths.length) + "" of "" + formatNumber(c.usageCount) + "" usages.</div>"" : """")
      : ""<div class='detail-empty'>Not referenced anywhere in the project.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(c.name) + ""</div><div class='item-meta' title='"" + escapeHtml(c.path || '') + ""'>"" + escapeHtml(c.kind || 'Data Type') + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Used by</span><span class='metric-value'>"" + formatNumber(c.usageCount) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Used in ("" + formatNumber(c.usageCount) + "")</h4>"" + usageSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<div class='alert-box warn'><div class='alert-title'>Placed outside Model</div><div class='alert-text'>These type definitions live under the Converters folder but are structurally Model types (enumerations / data types). They are surfaced here since that is where the project defines them.</div></div>""
    + ""<h3>Other Definitions ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

// Only allTags is kept now -- the flat driver/tag filter+sort state was removed when the
// driver list/tag list became tab-scoped (Phase 2 navigation shell); each tab reads directly
// from allTags via renderCommDriverTagsForDriverHtml instead of a separately maintained subset.
const commDriversTagsState = { allTags: [] };
const commDriversNavState = { driverRows: [], otherObjects: [], activeKey: null };

function renderCommDriverStationTagPreview(driverName, stationName, expectedTagCount) {
  const allTags = commDriversTagsState.allTags || [];
  const matches = allTags.filter(r => String(r.driver || '') === driverName && String(r.stationName || '') === stationName);
  if (!matches.length) {
    return ""<div class='detail-empty'>No tags found for this station in the loaded tag sample.</div>"";
  }

  const sorted = sortCommDriverTags(matches, 'usage-desc').slice(0, 20);
  const sampleNote = Number(expectedTagCount) > matches.length
    ? "" Station has "" + formatNumber(expectedTagCount) + "" tag(s) in total; only a subset may be present in the loaded/capped tag sample.""
    : """";
  const subtitle = ""<div class='chart-subtitle'>Showing "" + formatNumber(sorted.length) + "" of "" + formatNumber(matches.length) + "" tag(s) for this station found in the loaded sample."" + sampleNote + ""</div>"";
  const rows = sorted.map(r => ""<tr><td class='mono' title='"" + escapeHtml(r.path || '') + ""'>"" + escapeHtml(r.name || '-') + ""</td><td>"" + escapeHtml(r.variableType || '-') + ""</td><td>"" + escapeHtml(r.dataType || '-') + ""</td><td>"" + formatNumber(r.depth || 0) + ""</td><td>"" + formatNumber(r.usageCount || 0) + ""</td></tr>"").join('');
  return subtitle + ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Tag</th><th>Variable Type</th><th>DataType</th><th>Depth</th><th>Used</th></tr></thead><tbody>"" + rows + ""</tbody></table></div>"";
}

function renderCommDriverStationCard(driverName, station) {
  const vt = Array.isArray(station.variableTypeNames) ? station.variableTypeNames : [];
  const dt = Array.isArray(station.dataTypeNames) ? station.dataTypeNames : [];
  const vtCount = Number(station.variableTypeCount || vt.length);
  const dtCount = Number(station.dataTypeCount || dt.length);
  const vtText = vt.length ? vt.join(', ') : 'None';
  const dtText = dt.length ? dt.join(', ') : 'None';
  const vtSubtitle = vtCount > vt.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(vt.length) + "" of "" + formatNumber(vtCount) + "" variable types.</div>"" : """";
  const dtSubtitle = dtCount > dt.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(dt.length) + "" of "" + formatNumber(dtCount) + "" data types.</div>"" : """";

  return ""<div class='item-row'>""
    + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
    + ""<div class='item-left'><div class='item-name'>Station/device node: "" + escapeHtml(station.name || '-') + ""</div></div>""
    + ""<div class='item-right'>""
    + ""<div class='metric-badge'><span class='metric-label'>Tags</span><span class='metric-value'>"" + formatNumber(station.tagCount || 0) + ""</span></div>""
    + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
    + ""</div></div>""
    + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
    + ""<div class='detail-section detail-section-wide'><h4>DataTypes ("" + formatNumber(dtCount) + "")</h4><div class='mono' title='"" + escapeHtml(dtText) + ""'>"" + escapeHtml(dtText) + ""</div>"" + dtSubtitle + ""</div>""
    + ""<div class='detail-section detail-section-wide'><h4>Variable Types ("" + formatNumber(vtCount) + "")</h4><div class='mono' title='"" + escapeHtml(vtText) + ""'>"" + escapeHtml(vtText) + ""</div>"" + vtSubtitle + ""</div>""
    + ""<div class='detail-section detail-section-wide'><h4>Tags</h4>"" + renderCommDriverStationTagPreview(driverName, station.name, station.tagCount) + ""</div>""
    + ""</div></div>""
    + ""</div>"";
}

function renderCommDriverDriverCard(d) {
  const vt = Array.isArray(d.variableTypeNames) ? d.variableTypeNames : [];
  const dt = Array.isArray(d.dataTypeNames) ? d.dataTypeNames : [];
  const vtCount = Number(d.variableTypeCount || vt.length);
  const dtCount = Number(d.dataTypeCount || dt.length);
  const vtText = vt.length ? vt.join(', ') : 'None';
  const dtText = dt.length ? dt.join(', ') : 'None';
  const vtSubtitle = vtCount > vt.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(vt.length) + "" of "" + formatNumber(vtCount) + "" variable types.</div>"" : """";
  const dtSubtitle = dtCount > dt.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(dt.length) + "" of "" + formatNumber(dtCount) + "" data types.</div>"" : """";
  const stations = Array.isArray(d.stations) ? d.stations : [];

  // Station/device grouping is only shown when the project tree actually has a level to group
  // by -- otherwise this falls back to the original flat Variable Types/DataTypes lists, so no
  // hierarchy is ever implied where the backend could not safely derive one.
  const bodyHtml = stations.length
    ? ""<div class='detail-section detail-section-wide'><h4>Stations/Devices ("" + formatNumber(stations.length) + "")</h4>""
      + ""<div class='chart-subtitle'>Structure detected from project tree (direct child nodes of this driver). No station/device semantics beyond this grouping are inferred.</div>""
      + ""<div class='item-list'><div class='item-list-scroll'>"" + stations.map(s => renderCommDriverStationCard(d.name, s)).join('') + ""</div></div>""
      + ""</div>""
    : ""<div class='detail-section detail-section-wide'><h4>Variable Types ("" + formatNumber(vtCount) + "")</h4><div class='mono' title='"" + escapeHtml(vtText) + ""'>"" + escapeHtml(vtText) + ""</div>"" + vtSubtitle + ""</div>""
      + ""<div class='detail-section detail-section-wide'><h4>DataTypes ("" + formatNumber(dtCount) + "")</h4><div class='mono' title='"" + escapeHtml(dtText) + ""'>"" + escapeHtml(dtText) + ""</div>"" + dtSubtitle + ""</div>""
      + ""<div class='chart-subtitle'>No station/device structure detected under this driver in the project tree.</div>"";

  const stationsBadge = stations.length
    ? ""<div class='metric-badge'><span class='metric-label'>Stations</span><span class='metric-value'>"" + formatNumber(stations.length) + ""</span></div>""
    : '';

  return ""<div class='item-row'>""
    + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
    + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(d.name || '-') + ""</div></div>""
    + ""<div class='item-right'>""
    + ""<div class='metric-badge'><span class='metric-label'>Tags</span><span class='metric-value'>"" + formatNumber(d.tagCount || 0) + ""</span></div>""
    + stationsBadge
    + ""<div class='metric-badge'><span class='metric-label'>DataTypes</span><span class='metric-value'>"" + formatNumber(dtCount) + ""</span></div>""
    + ""<div class='metric-badge'><span class='metric-label'>VarTypes</span><span class='metric-value'>"" + formatNumber(vtCount) + ""</span></div>""
    + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
    + ""</div></div>""
    + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
    + bodyHtml
    + ""</div></div>""
    + ""</div>"";
}

function sortCommDriverDrivers(items, mode) {
  const rows = items.slice();
  if (mode === 'name-asc') {
    rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')));
  } else {
    rows.sort((a, b) => Number(b.tagCount || 0) - Number(a.tagCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
  }
  return rows;
}

function sortCommDriverTags(items, mode) {
  const rows = items.slice();
  if (mode === 'depth-desc') {
    rows.sort((a, b) => Number(b.depth || 0) - Number(a.depth || 0) || Number(b.usageCount || 0) - Number(a.usageCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
  } else if (mode === 'name-asc') {
    rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')));
  } else {
    rows.sort((a, b) => Number(b.usageCount || 0) - Number(a.usageCount || 0) || Number(b.depth || 0) - Number(a.depth || 0) || String(a.name || '').localeCompare(String(b.name || '')));
  }
  return rows;
}

function renderCommDriverTagRow(r) {
  const sources = Array.isArray(r.usageSources) ? r.usageSources : [];
  const sourceTitle = sources.map(s => (s.path || '') + (s.category ? ' [' + s.category + ']' : '') + (s.name ? ' -> ' + s.name : '')).join(' | ');
  const sourceCells = sources.slice(0, 2).map(s => escapeHtml(abbreviatePathMiddle(s.path || '', 70)) + (s.category ? "" <span class='item-meta'>"" + escapeHtml(s.category) + ""</span>"" : '')).join(' ; ');
  return ""<tr><td class='mono' title='"" + escapeHtml(r.path || '') + ""'>"" + escapeHtml(r.name || '-') + ""</td><td>"" + escapeHtml(r.driver || '-') + ""</td><td>"" + escapeHtml(r.variableType || '-') + ""</td><td>"" + escapeHtml(r.dataType || '-') + ""</td><td>"" + formatNumber(r.depth || 0) + ""</td><td>"" + formatNumber(r.usageCount || 0) + ""</td><td class='mono' title='"" + escapeHtml(sourceTitle) + ""'>"" + (sourceCells || '-') + ""</td></tr>"";
}

// Renders the Tags panel scoped to ONE driver (called on tab select and on query/sort change).
// totalTagCountForDriver is the driver's true, uncapped tag total (d.tagCount); the loaded
// sample is commDriversTagsState.allTags filtered to this driver, which may be a subset of it.
function renderCommDriverTagsForDriverHtml(driverName, totalTagCountForDriver) {
  const statsHost = document.getElementById('commdrivers-tag-stats');
  const resultsHost = document.getElementById('commdrivers-tag-results');
  if (!statsHost || !resultsHost) return;

  const scoped = (commDriversTagsState.allTags || []).filter(r => String(r.driver || '') === driverName);

  const queryEl = document.getElementById('commdrivers-tag-filter-query');
  const sortEl = document.getElementById('commdrivers-tag-filter-sort');
  const query = (queryEl ? String(queryEl.value || '') : '').trim().toLowerCase();
  const sort = sortEl ? String(sortEl.value || 'usage-desc') : 'usage-desc';

  let rows = scoped.slice();
  if (query) {
    rows = rows.filter(r => String(r.name || '').toLowerCase().includes(query));
  }
  rows = sortCommDriverTags(rows, sort);

  if (!rows.length) {
    statsHost.innerHTML = ""<div class='detail-empty'>No tags for this driver match current filters in the loaded sample.</div>"";
    resultsHost.innerHTML = """";
    return;
  }

  const sampleNote = Number(totalTagCountForDriver) > scoped.length
    ? "" This driver has "" + formatNumber(totalTagCountForDriver) + "" tag(s) in total; only a subset may be present in the loaded/capped project-wide tag sample.""
    : """";
  statsHost.innerHTML = ""<div class='type-map-stats-pill'>Showing <strong>"" + formatNumber(rows.length) + ""</strong> of <strong>"" + formatNumber(scoped.length) + ""</strong> loaded tag(s) for this driver."" + sampleNote + ""</div>"";
  resultsHost.innerHTML = ""<div class='table-scroll table-scroll-entities'><table class='table-compact'><thead><tr><th>Tag</th><th>Driver</th><th>Variable Type</th><th>DataType</th><th>Depth</th><th>Used</th><th>Cross-Reference</th></tr></thead><tbody>"" + rows.map(renderCommDriverTagRow).join('') + ""</tbody></table></div>"";
}

// Attaches query/sort listeners scoped to one driver tab; re-attached on every tab render since
// the toolbar DOM is recreated each time a tab is selected.
function wireCommDriverTagControls(driverName, totalTagCountForDriver) {
  const queryEl = document.getElementById('commdrivers-tag-filter-query');
  const sortEl = document.getElementById('commdrivers-tag-filter-sort');
  const rerender = () => renderCommDriverTagsForDriverHtml(driverName, totalTagCountForDriver);
  if (queryEl) queryEl.addEventListener('input', rerender);
  if (sortEl) sortEl.addEventListener('change', rerender);
  rerender();
}

function renderCommDriverTagsToolbarHtml(driver) {
  const totalTagCountForDriver = Number((driver && driver.tagCount) || 0);
  const subtitle = totalTagCountForDriver > 0
    ? ""<div class='detail-subtitle'>Ordered by usage/depth. Filters and sorting below apply only to the loaded project-wide tag sample, not the full project.</div>""
    : """";
  return ""<h4>Tags</h4>""
    + subtitle
    + ""<div class='type-map-toolbar commdrivers-tag-toolbar'>""
    + ""<input id='commdrivers-tag-filter-query' class='type-map-input' type='text' placeholder='Filter by tag name...' autocomplete='off'>""
    + ""<select id='commdrivers-tag-filter-sort' class='type-map-select'>""
    + ""<option value='usage-desc'>Usage (high to low)</option>""
    + ""<option value='depth-desc'>Depth (high to low)</option>""
    + ""<option value='name-asc'>Name (A-Z)</option>""
    + ""</select>""
    + ""</div>""
    + ""<div id='commdrivers-tag-stats' class='type-map-stats'></div>""
    + ""<div id='commdrivers-tag-results'></div>"";
}

function renderCommDriverOtherObjectsHtml(otherObjects) {
  const rows = Array.isArray(otherObjects) ? otherObjects : [];
  if (!rows.length) {
    return ""<div class='detail-section'><h4>Other objects under CommDrivers</h4><div class='detail-empty'>No non-driver objects detected directly under CommDrivers.</div></div>"";
  }

  const items = rows.map(o => (
    ""<div class='item-row'>""
    + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
    + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(o.name || '-') + ""</div></div>""
    + ""<div class='item-right'>""
    + ""<div class='metric-badge'><span class='metric-label'>Children</span><span class='metric-value'>"" + formatNumber(o.childCount || 0) + ""</span></div>""
    + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
    + ""</div></div>""
    + ""<div class='item-details'><div class='detail-content'>""
    + ""<div class='detail-kv'>""
    + ""<div class='detail-kv-row'><span>Path</span><strong class='mono' title='"" + escapeHtml(o.path || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(o.path || '', 90)) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Detected Type</span><strong>"" + escapeHtml(o.detectedType || '-') + ""</strong></div>""
    + ""</div>""
    + ""<div class='detail-subtitle'>Not a communication driver -- this is a child object under CommDrivers. May be intentional; review recommended.</div>""
    + ""</div></div>""
    + ""</div>""
  )).join('');

  return ""<div class='detail-section'><h4>Other objects under CommDrivers ("" + formatNumber(rows.length) + "")</h4>""
    + ""<div class='chart-subtitle'>These objects are not CommunicationDriver-derived nodes and are never counted as drivers.</div>""
    + ""<div class='item-list'><div class='item-list-scroll'>"" + items + ""</div></div>""
    + ""</div>"";
}

function renderCommDriverUnresolvedCandidatesHtml(candidates, totalCount) {
  const rows = Array.isArray(candidates) ? candidates : [];
  if (!rows.length) {
    return ""<div class='detail-section'><h4>Unresolved CommDrivers Dynamic-Link Candidates</h4>""
      + ""<div class='detail-empty'>No unresolved dynamic-link candidates targeting CommDrivers were detected.</div></div>"";
  }

  const subtitle = Number(totalCount) > rows.length
    ? ""<div class='detail-subtitle'>Showing "" + formatNumber(rows.length) + "" of "" + formatNumber(totalCount) + "" unresolved candidate(s).</div>""
    : """";

  const tableRows = rows.map(c => (
    ""<tr><td class='mono' title='"" + escapeHtml(c.sourcePath || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(c.sourcePath || '', 60)) + ""</td>""
    + ""<td>"" + escapeHtml(c.sourceCategory || '-') + ""</td>""
    + ""<td>"" + escapeHtml(c.sourceName || '-') + ""</td>""
    + ""<td class='mono' title='"" + escapeHtml(c.rawTarget || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(c.rawTarget || '', 60)) + ""</td></tr>""
  )).join('');

  return ""<div class='detail-section'><h4>Unresolved CommDrivers Dynamic-Link Candidates ("" + formatNumber(totalCount) + "")</h4>""
    + subtitle
    + ""<div class='chart-subtitle'>Concrete DynamicLink targets whose raw text points under CommDrivers but which could not be resolved to a project node. This is NOT proof that the referenced tag is missing or deleted.</div>""
    + ""<div class='table-scroll table-scroll-entities'><table class='table-compact'><thead><tr><th>Source Path</th><th>Source Category</th><th>Source Name</th><th>Raw Target</th></tr></thead><tbody>"" + tableRows + ""</tbody></table></div>""
    + ""</div>"";
}

function renderCommDriverNavTabsHtml(driverRows, hasOtherTab) {
  const driverButtons = driverRows.map(d => (
    ""<button type='button' class='commdrivers-tab' data-tab-key='"" + escapeHtml(d.name || '') + ""' role='tab' aria-selected='false'>""
    + escapeHtml(d.name || '-')
    + ""<span class='commdrivers-tab-count'>"" + formatNumber(d.tagCount || 0) + ""</span>""
    + ""</button>""
  )).join('');
  const otherButton = hasOtherTab
    ? ""<button type='button' class='commdrivers-tab' data-tab-key='__other__' role='tab' aria-selected='false'>Other</button>""
    : '';

  if (!driverButtons && !otherButton) {
    return ""<div class='detail-empty'>No communication drivers or other CommDrivers objects detected.</div>"";
  }

  return ""<div class='commdrivers-tabs' id='commdrivers-tabs' role='tablist'>"" + driverButtons + otherButton + ""</div>""
    + ""<div class='commdrivers-tabpanel' id='commdrivers-tabpanel' role='tabpanel'></div>"";
}

// Renders the content for one selected tab into #commdrivers-tabpanel. Driver tabs reuse the
// existing driver-card accordion (stations/tag preview unchanged) plus a driver-scoped Tags
// toolbar; the Other tab reuses the existing non-driver-objects renderer unchanged.
function selectCommDriverTab(key) {
  if (!key) return;
  commDriversNavState.activeKey = key;

  const tabsHost = document.getElementById('commdrivers-tabs');
  if (tabsHost) {
    tabsHost.querySelectorAll('.commdrivers-tab').forEach(btn => {
      const isActive = btn.getAttribute('data-tab-key') === key;
      btn.classList.toggle('active', isActive);
      btn.setAttribute('aria-selected', isActive ? 'true' : 'false');
    });
  }

  const panel = document.getElementById('commdrivers-tabpanel');
  if (!panel) return;

  if (key === '__other__') {
    panel.innerHTML = renderCommDriverOtherObjectsHtml(commDriversNavState.otherObjects);
    return;
  }

  const driver = commDriversNavState.driverRows.find(d => String(d.name || '') === key);
  if (!driver) {
    panel.innerHTML = ""<div class='detail-empty'>Driver data unavailable.</div>"";
    return;
  }

  panel.innerHTML = ""<div class='item-list'><div class='item-list-scroll'>"" + renderCommDriverDriverCard(driver) + ""</div></div>""
    + ""<div class='detail-section detail-section-wide'>"" + renderCommDriverTagsToolbarHtml(driver) + ""</div>"";

  wireCommDriverTagControls(driver.name, Number(driver.tagCount || 0));
}

// Wires the tab-click delegation (once per section load) and selects the initial tab -- the
// first driver in tag-count order, or Other when there are no real drivers at all.
function initCommDriverNav(section) {
  const s = section || {};
  commDriversNavState.driverRows = sortCommDriverDrivers(Array.isArray(s.driverDetails) ? s.driverDetails : [], 'tags-desc');
  commDriversNavState.otherObjects = Array.isArray(s.otherObjects) ? s.otherObjects : [];
  commDriversNavState.activeKey = null;

  const tabsHost = document.getElementById('commdrivers-tabs');
  if (tabsHost) {
    tabsHost.addEventListener('click', event => {
      const btn = event.target.closest('.commdrivers-tab');
      if (!btn) return;
      selectCommDriverTab(btn.getAttribute('data-tab-key'));
    });
  }

  const initialKey = commDriversNavState.driverRows.length
    ? commDriversNavState.driverRows[0].name
    : (commDriversNavState.otherObjects.length ? '__other__' : null);
  if (initialKey) selectCommDriverTab(initialKey);
}

function renderCommDriversSection(section) {
  const s = section || {};
  const anomalies = s.anomalies || {};
  const referenceSummary = s.referenceSummary || {};
  const unresolvedCandidates = Array.isArray(s.unresolvedCandidates) ? s.unresolvedCandidates : [];
  // Sorted here (same order used by initCommDriverNav) so the rendered tab order always matches
  // the order the nav state resolves its initial/active tab from.
  const driverRows = sortCommDriverDrivers(Array.isArray(s.driverDetails) ? s.driverDetails : [], 'tags-desc');
  const otherObjects = Array.isArray(s.otherObjects) ? s.otherObjects : [];

  const anomaliesHtml = ""<div class='detail-section'><h4>Anomalies</h4><div class='detail-kv'>""
    + ""<div class='detail-kv-row'><span>Broken Links</span><strong>"" + formatNumber(anomalies.brokenDynamicLinks || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Unresolved Links</span><strong>"" + formatNumber(anomalies.unresolvedDynamicLinks || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Drivers w/o Tags</span><strong>"" + formatNumber(anomalies.driversWithoutTags || 0) + ""</strong></div>""
    + ""</div></div>"";

  const referenceSummaryHtml = ""<div class='detail-section'><h4>Incoming Dynamic-Link Reference Summary</h4><div class='detail-kv'>""
    + ""<div class='detail-kv-row'><span>Tags with detected incoming dynamic-link references</span><strong>"" + formatNumber(referenceSummary.tagsWithIncomingRef || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Tags without detected incoming dynamic-link references</span><strong>"" + formatNumber(referenceSummary.tagsWithoutIncomingRef || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Resolved incoming dynamic-link references (total)</span><strong>"" + formatNumber(referenceSummary.resolvedReferenceCount || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Unresolved CommDrivers dynamic-link candidates (total)</span><strong>"" + formatNumber(referenceSummary.unresolvedCandidateCount || 0) + ""</strong></div>""
    + ""</div>""
    + ""<div class='chart-subtitle'>"" + escapeHtml(referenceSummary.limitationNote || 'Coverage is limited to DynamicLink references that resolve at design time. Other reference mechanisms are not detected by this metric.') + ""</div>""
    + ""</div>"";

  // Navigation shell: one tab per real driver plus an Other tab when non-driver objects exist.
  // Driver/tag lists are no longer rendered as separate stacked sections -- their content now
  // lives inside the active tab, populated by initCommDriverNav after this HTML is inserted.
  const navHtml = ""<div class='detail-section commdrivers-nav-section'>"" + renderCommDriverNavTabsHtml(driverRows, otherObjects.length > 0) + ""</div>"";

  const unresolvedCandidatesHtml = renderCommDriverUnresolvedCandidatesHtml(unresolvedCandidates, Number(referenceSummary.unresolvedCandidateCount || 0));

  return ""<div class='detail-stack'>"" + anomaliesHtml + referenceSummaryHtml + navHtml + unresolvedCandidatesHtml + ""</div>"";
}

function ensureCommDriversLoaded() {
  if (commDriversState.loaded || commDriversState.loading) return;
  const host = document.getElementById('commdrivers-host');
  if (!host) return;
  commDriversState.loading = true;
  loadSectionByKey('commdrivers')
    .then(section => {
      commDriversState.loading = false;
      if (!section) {
        host.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Data unavailable</div><div class='alert-text'>No CommDrivers section was generated for this project.</div></div>"";
        return;
      }
      commDriversState.loaded = true;
      commDriversState.data = section;
      host.innerHTML = renderCommDriversSection(section);
      // allTags must be populated before the nav selects its initial tab, since the tab's Tags
      // panel and the station tag preview both read from it synchronously on first render.
      commDriversTagsState.allTags = Array.isArray(section.tagDetails) ? section.tagDetails.slice() : [];
      initCommDriverNav(section);
    })
    .catch(() => {
      commDriversState.loading = false;
      host.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load CommDrivers data.</div></div>"";
    });
}

function renderOpcUaSection(section) {
  const s = section || {};
  const anomalies = s.anomalies || {};
  const entities = Array.isArray(s.entities) ? s.entities : [];

  const anomaliesHtml = ""<div class='detail-section'><h4>Anomalies</h4><div class='detail-kv'>""
    + ""<div class='detail-kv-row'><span>Broken Links</span><strong>"" + formatNumber(anomalies.brokenDynamicLinks || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Unresolved Links</span><strong>"" + formatNumber(anomalies.unresolvedDynamicLinks || 0) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Subscriptions w/o Client</span><strong>"" + formatNumber(anomalies.subscriptionsWithoutClient || 0) + ""</strong></div>""
    + ""</div></div>"";

  if (!entities.length) {
    return anomaliesHtml + ""<div class='detail-empty'>No OPC-UA Clients, Servers or Subscriptions detected.</div>"";
  }

  return anomaliesHtml + renderOpcUaEntitiesToolbarHtml(entities.length);
}

const opcuaEntitiesState = { allEntities: [], filtered: [], query: '', kind: 'all', sort: 'name-asc' };

function sortOpcUaEntities(items, mode) {
  const rows = items.slice();
  if (mode === 'kind') {
    rows.sort((a, b) => String(a.kind || '').localeCompare(String(b.kind || '')) || String(a.name || '').localeCompare(String(b.name || '')));
  } else {
    rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')));
  }
  return rows;
}

function renderOpcUaEntityCard(e) {
  const kind = String(e.kind || 'Unknown');
  const noClientBadge = (kind === 'Subscription' && e.hasClient === false)
    ? "" <span class='mini-badge mini-badge-unresolved'>No client configured</span>""
    : '';

  return ""<div class='item-row'>""
    + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
    + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(e.name || '-')
    + "" <span class='item-meta'>"" + escapeHtml(kind) + ""</span>"" + noClientBadge
    + ""</div></div>""
    + ""<div class='item-right'>""
    + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
    + ""</div></div>""
    + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
    + ""<div class='detail-section detail-section-wide'><h4>Path</h4><div class='mono usage-path'>"" + escapeHtml(e.path || 'Path to node not available') + ""</div></div>""
    + ""</div></div>""
    + ""</div>"";
}

function renderOpcUaEntitiesResults() {
  const statsHost = document.getElementById('opcua-entities-stats');
  const resultsHost = document.getElementById('opcua-entities-results');
  if (!statsHost || !resultsHost) return;

  const rows = opcuaEntitiesState.filtered;
  if (!rows.length) {
    statsHost.innerHTML = ""<div class='detail-empty'>No OPC-UA entities match current filters.</div>"";
    resultsHost.innerHTML = """";
    return;
  }

  statsHost.innerHTML = ""<div class='type-map-stats-pill'>Showing <strong>"" + formatNumber(rows.length) + ""</strong> of <strong>"" + formatNumber(opcuaEntitiesState.allEntities.length) + ""</strong> entity(ies)</div>"";
  resultsHost.innerHTML = ""<div class='item-list'><div class='item-list-scroll'>"" + rows.map(renderOpcUaEntityCard).join('') + ""</div></div>"";
}

function updateOpcUaEntityFilters() {
  const queryEl = document.getElementById('opcua-filter-query');
  const kindEl = document.getElementById('opcua-filter-kind');
  const sortEl = document.getElementById('opcua-filter-sort');

  opcuaEntitiesState.query = (queryEl ? String(queryEl.value || '') : '').trim().toLowerCase();
  opcuaEntitiesState.kind = kindEl ? String(kindEl.value || 'all') : 'all';
  opcuaEntitiesState.sort = sortEl ? String(sortEl.value || 'name-asc') : 'name-asc';

  let rows = opcuaEntitiesState.allEntities.slice();
  if (opcuaEntitiesState.query) {
    rows = rows.filter(e => String(e.name || '').toLowerCase().includes(opcuaEntitiesState.query) || String(e.path || '').toLowerCase().includes(opcuaEntitiesState.query));
  }
  if (opcuaEntitiesState.kind !== 'all') {
    rows = rows.filter(e => String(e.kind || '').toLowerCase() === opcuaEntitiesState.kind);
  }

  opcuaEntitiesState.filtered = sortOpcUaEntities(rows, opcuaEntitiesState.sort);
  renderOpcUaEntitiesResults();
}

function initOpcUaEntityControls(section) {
  const s = section || {};
  opcuaEntitiesState.allEntities = Array.isArray(s.entities) ? s.entities.slice() : [];
  opcuaEntitiesState.filtered = opcuaEntitiesState.allEntities.slice();

  const queryEl = document.getElementById('opcua-filter-query');
  const kindEl = document.getElementById('opcua-filter-kind');
  const sortEl = document.getElementById('opcua-filter-sort');
  if (queryEl) queryEl.addEventListener('input', updateOpcUaEntityFilters);
  if (kindEl) kindEl.addEventListener('change', updateOpcUaEntityFilters);
  if (sortEl) sortEl.addEventListener('change', updateOpcUaEntityFilters);

  updateOpcUaEntityFilters();
}

function renderOpcUaEntitiesToolbarHtml(count) {
  return ""<h3>OPC-UA Entities ("" + formatNumber(count) + "")</h3>""
    + ""<div class='type-map-toolbar'>""
    + ""<input id='opcua-filter-query' class='type-map-input' type='text' placeholder='Filter by name or path...' autocomplete='off'>""
    + ""<select id='opcua-filter-kind' class='type-map-select'>""
    + ""<option value='all'>All kinds</option>""
    + ""<option value='client'>Clients</option>""
    + ""<option value='server'>Servers</option>""
    + ""<option value='subscription'>Subscriptions</option>""
    + ""</select>""
    + ""<select id='opcua-filter-sort' class='type-map-select'>""
    + ""<option value='name-asc'>Name (A-Z)</option>""
    + ""<option value='kind'>Kind</option>""
    + ""</select>""
    + ""</div>""
    + ""<div id='opcua-entities-stats' class='type-map-stats'></div>""
    + ""<div id='opcua-entities-results'></div>"";
}

const netLogicTypesState = { allTypes: [], filtered: [], topFilesByPath: {}, query: '', sort: 'instances-desc', placement: 'all' };

function findNetLogicTopFile(sourceFile, topFilesByPath) {
  if (!sourceFile) return null;
  return topFilesByPath[String(sourceFile).toLowerCase()] || null;
}

// Source-file impact only reflects the compiled class actually executing at runtime -
// a type whose instances are all design-time placeholders has no meaningful impact value.
function netLogicTypeHasRuntimeInstance(t) {
  const instances = Array.isArray(t.instances) ? t.instances : [];
  return instances.some(inst => inst && inst.category && inst.category !== 'netlogic');
}

function netLogicTypeTopFile(t) {
  if (!netLogicTypeHasRuntimeInstance(t)) return null;
  return findNetLogicTopFile(t.sourceFile, netLogicTypesState.topFilesByPath);
}

function renderNetLogicTypeCard(t) {
  const instances = Array.isArray(t.instances) ? t.instances : [];
  const instanceCount = Number(t.instanceCount || 0);
  const placementLabel = netLogicTypeHasRuntimeInstance(t) ? 'Runtime' : 'Design-time only';
  const instanceRows = instances.length
    ? ""<ul class='detail-list usage-path-list'>"" + instances.map(inst =>
        ""<li>""
        + ""<div class='mono'>"" + escapeHtml(inst.name || '-')
        + (inst.category ? "" <span class='item-meta'>("" + escapeHtml(String(inst.category).toUpperCase()) + "")</span>"" : """")
        + ""</div>""
        + ""<div class='mono usage-path'>"" + escapeHtml(inst.path || 'Path to node not available') + ""</div>""
        + ""</li>"").join('') + ""</ul>""
      + (instanceCount > instances.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(instances.length) + "" of "" + formatNumber(instanceCount) + "" instances.</div>"" : """")
    : ""<div class='detail-empty'>No instances found.</div>"";

  const hasRuntimeInstance = netLogicTypeHasRuntimeInstance(t);
  const topFile = netLogicTypeTopFile(t);
  const sourceHtml = t.sourceFile
    ? ""<div class='detail-kv'>""
      + ""<div class='detail-kv-row'><span>Source file</span><strong class='mono'>"" + escapeHtml(t.sourceFile) + ""</strong></div>""
      + (hasRuntimeInstance
        ? (topFile
            ? ""<div class='detail-kv-row'><span>Review point</span><strong>Top impact file</strong></div>""
              + ""<div class='detail-kv-row'><span>Impact</span><strong>"" + formatNumber(topFile.impact || 0) + ""</strong></div>""
              + ""<div class='detail-kv-row'><span>Methods</span><strong>"" + formatNumber(topFile.methods || 0) + ""</strong></div>""
              + ""<div class='detail-kv-row'><span>Complexity</span><strong>"" + formatNumber(topFile.complexity || 0) + ""</strong></div>""
            : """")
        : ""<div class='detail-kv-row'><span>Runtime impact</span><strong>Not applicable (design-time only)</strong></div>"")
      + ""</div>""
    : """";

  return ""<div class='item-row'>""
    + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
    + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(t.name || '-')
    + "" <span class='item-meta' title='Design-time: only placed under the NetLogic library folder. Runtime: attached to at least one live Model/UI object.'>"" + escapeHtml(placementLabel) + ""</span>""
    + ""</div></div>""
    + ""<div class='item-right'>""
    + ""<div class='metric-badge'><span class='metric-label'>Instances</span><span class='metric-value'>"" + formatNumber(instanceCount) + ""</span></div>""
    + (topFile ? ""<div class='metric-badge'><span class='metric-label'>Impact</span><span class='metric-value'>"" + formatNumber(topFile.impact || 0) + ""</span></div>"" : """")
    + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
    + ""</div></div>""
    + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
    + (sourceHtml ? ""<div class='detail-section'><h4>Source file</h4>"" + sourceHtml + ""</div>"" : """")
    + ""<div class='detail-section detail-section-wide'><h4>Instances ("" + formatNumber(instanceCount) + "")</h4>"" + instanceRows + ""</div>""
    + ""</div></div>""
    + ""</div>"";
}

function sortNetLogicTypes(items, mode) {
  const rows = items.slice();
  if (mode === 'name-asc') {
    rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')));
  } else if (mode === 'impact-desc') {
    rows.sort((a, b) => {
      const impactA = (netLogicTypeTopFile(a) || {}).impact || 0;
      const impactB = (netLogicTypeTopFile(b) || {}).impact || 0;
      return Number(impactB) - Number(impactA) || Number(b.instanceCount || 0) - Number(a.instanceCount || 0) || String(a.name || '').localeCompare(String(b.name || ''));
    });
  } else {
    rows.sort((a, b) => Number(b.instanceCount || 0) - Number(a.instanceCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
  }
  return rows;
}

function renderNetLogicTypesResults() {
  const statsHost = document.getElementById('netlogic-types-stats');
  const resultsHost = document.getElementById('netlogic-types-results');
  if (!statsHost || !resultsHost) return;

  const rows = netLogicTypesState.filtered;
  if (!rows.length) {
    statsHost.innerHTML = ""<div class='detail-empty'>No NetLogic types match current filters.</div>"";
    resultsHost.innerHTML = """";
    return;
  }

  statsHost.innerHTML = ""<div class='type-map-stats-pill'>Showing <strong>"" + formatNumber(rows.length) + ""</strong> of <strong>"" + formatNumber(netLogicTypesState.allTypes.length) + ""</strong> NetLogic type(s)</div>"";
  resultsHost.innerHTML = ""<div class='item-list'><div class='item-list-scroll'>"" + rows.map(renderNetLogicTypeCard).join('') + ""</div></div>"";
}

function updateNetLogicTypeFilters() {
  const queryEl = document.getElementById('netlogic-filter-query');
  const sortEl = document.getElementById('netlogic-filter-sort');
  const placementEl = document.getElementById('netlogic-filter-placement');

  netLogicTypesState.query = (queryEl ? String(queryEl.value || '') : '').trim().toLowerCase();
  netLogicTypesState.sort = sortEl ? String(sortEl.value || 'instances-desc') : 'instances-desc';
  netLogicTypesState.placement = placementEl ? String(placementEl.value || 'all') : 'all';

  let rows = netLogicTypesState.allTypes.slice();
  if (netLogicTypesState.query) {
    rows = rows.filter(t => String(t.name || '').toLowerCase().includes(netLogicTypesState.query));
  }
  if (netLogicTypesState.placement === 'runtime') {
    rows = rows.filter(t => netLogicTypeHasRuntimeInstance(t));
  } else if (netLogicTypesState.placement === 'design') {
    rows = rows.filter(t => !netLogicTypeHasRuntimeInstance(t));
  }

  netLogicTypesState.filtered = sortNetLogicTypes(rows, netLogicTypesState.sort);
  renderNetLogicTypesResults();
}

function initNetLogicTypesControls(section) {
  const s = section || {};
  const payload = reportRuntimeState.payload || {};
  const solution = payload.solutionInsights || {};
  const topFiles = Array.isArray(solution.topFiles) ? solution.topFiles : [];

  netLogicTypesState.topFilesByPath = {};
  topFiles.forEach(f => { if (f && f.path) netLogicTypesState.topFilesByPath[String(f.path).toLowerCase()] = f; });

  netLogicTypesState.allTypes = Array.isArray(s.types) ? s.types.slice() : [];
  netLogicTypesState.filtered = netLogicTypesState.allTypes.slice();

  const queryEl = document.getElementById('netlogic-filter-query');
  const sortEl = document.getElementById('netlogic-filter-sort');
  const placementEl = document.getElementById('netlogic-filter-placement');
  if (queryEl) queryEl.addEventListener('input', updateNetLogicTypeFilters);
  if (sortEl) sortEl.addEventListener('change', updateNetLogicTypeFilters);
  if (placementEl) placementEl.addEventListener('change', updateNetLogicTypeFilters);

  updateNetLogicTypeFilters();
}

function renderNetLogicTypesToolbarHtml(count) {
  return ""<h3>NetLogic Types ("" + formatNumber(count) + "")</h3>""
    + ""<div class='type-map-toolbar'>""
    + ""<input id='netlogic-filter-query' class='type-map-input' type='text' placeholder='Filter by type name...' autocomplete='off'>""
    + ""<select id='netlogic-filter-placement' class='type-map-select'>""
    + ""<option value='all'>All types</option>""
    + ""<option value='runtime'>Runtime only</option>""
    + ""<option value='design'>Design-time only</option>""
    + ""</select>""
    + ""<select id='netlogic-filter-sort' class='type-map-select'>""
    + ""<option value='instances-desc'>Instances (high to low)</option>""
    + ""<option value='impact-desc'>Impact (high to low)</option>""
    + ""<option value='name-asc'>Name (A-Z)</option>""
    + ""</select>""
    + ""</div>""
    + ""<div id='netlogic-types-stats' class='type-map-stats'></div>""
    + ""<div id='netlogic-types-results'></div>"";
}

function renderNetLogicLazy(section) {
  const s = section || {};
  const types = Array.isArray(s.types) ? s.types : [];

  if (!types.length) {
    return ""<div class='detail-empty'>No NetLogic types detected.</div>"";
  }

  const impactNote = ""<div class='alert-box' style='margin-top:16px'><div class='alert-title'>What does Impact mean?</div><div class='alert-text'>Impact is a relative score that highlights which NetLogic source files are larger and more complex than others, so you know where to focus code review and testing effort first. It's a comparison aid, not an absolute unit of measurement, and only applies to types with at least one runtime instance.</div></div>"";

  return impactNote + renderNetLogicTypesToolbarHtml(types.length);
}

function ensureCategoryLazyLoaded(categoryKey) {
  const host = document.getElementById('category-lazy-' + categoryKey);
  if (!host || host.getAttribute('data-loaded') === '1') return;
  host.innerHTML = ""<div class='detail-empty'>Loading details...</div>"";

  loadSectionByKey(categoryKey)
    .then(section => {
      if (!section) {
        host.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Detail not generated yet</div><div class='alert-text'>Run diagnostics to generate this category section.</div></div>"";
        return;
      }

      host.innerHTML = ""<div class='detail-empty'>No detail renderer for this category.</div>"";

      host.setAttribute('data-loaded', '1');
    })
    .catch(() => {
      host.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load category detail.</div></div>"";
    });
}

function buildGlobalOverviewModel(data) {
  const categoryMetrics = data.categoryMetrics || {};
  const crossCutting = data.crossCutting || {};
  const overview = data.overview || {};
  const categories = getProjectCategoryDefs(data).map(def => {
    const item = categoryMetrics[def.key] || {};
    return {
      key: def.key,
      label: def.label,
      nodes: Number(item.nodes || 0),
      folders: Number(item.folders || 0),
      objects: Number(item.objects || 0),
      variables: Number(item.variables || 0),
      dynamicLinks: Number(item.dynamicLinks || 0),
      netLogic: Number(item.netLogic || 0)
    };
  });

  const totalNodes = categories.reduce((sum, item) => sum + item.nodes, 0);
  const topCategory = categories.slice().sort((a, b) => b.nodes - a.nodes)[0] || { label: 'N/A', nodes: 0 };
  const topShare = totalNodes > 0 ? ((topCategory.nodes * 100) / totalNodes) : 0;
  const elementComposition = [
    { name: 'Folders', count: categories.reduce((sum, item) => sum + item.folders, 0) },
    { name: 'Objects', count: categories.reduce((sum, item) => sum + item.objects, 0) },
    { name: 'Variables', count: categories.reduce((sum, item) => sum + item.variables, 0) }
  ];

  const categoryDistribution = categories
    .filter(item => item.nodes > 0)
    .map(item => ({
      name: item.label,
      count: item.nodes,
      percentage: totalNodes > 0 ? (item.nodes * 100) / totalNodes : 0
    }))
    .sort((a, b) => b.count - a.count);

  const dynamicLinkDistribution = categories
    .filter(item => item.dynamicLinks > 0)
    .map(item => ({
      name: item.label,
      count: item.dynamicLinks,
      percentage: Number(crossCutting.dynamicLinksTotal || 0) > 0 ? (item.dynamicLinks * 100) / Number(crossCutting.dynamicLinksTotal || 0) : 0
    }))
    .sort((a, b) => b.count - a.count);

  return {
    totalNodes,
    topCategory: topCategory.label,
    topShare,
    totalDynamicLinks: Number(crossCutting.dynamicLinksTotal || 0),
    totalNetLogic: Number(crossCutting.netLogicTotal || 0),
    screensCount: Number(overview.screensCount || 0),
    commDriverTags: Number(overview.commDriverTags || 0),
    categoryDistribution,
    elementComposition,
    dynamicLinkDistribution
  };
}

function renderGlobalOverviewSection(data) {
  const model = buildGlobalOverviewModel(data);

  return ""<section class='global-overview'>""
    + ""<div class='section-title-block'><h2>Overview</h2><p>Project-wide distribution across categories and element types.</p></div>""
    + ""<div class='summary'>""
    + summaryCard('Project Nodes', formatNumber(model.totalNodes))
    + summaryCard('Dynamic Links', formatNumber(model.totalDynamicLinks))
    + summaryCard('NetLogic', formatNumber(model.totalNetLogic))
    + summaryCard('Screens', formatNumber(model.screensCount))
    + summaryCard('CommDriver Tags', formatNumber(model.commDriverTags))
    + ""</div>""
    + ""<div class='alert-box'><div class='alert-title'>Primary concentration</div><div class='alert-text'>Largest category: <strong>"" + escapeHtml(model.topCategory) + ""</strong> with <strong>"" + formatNumber(model.topShare.toFixed(1)) + ""%</strong> of total nodes.</div></div>""
    + ""</section>"";
}

function initGlobalOverviewSection(data) {
  // Charts temporarily disabled: overview remains KPI-focused.
}

function renderUiAccordionTabs(data) {
  return ""<div class='ui-accordion-tabs'>""
    + ""<div class='nav-tabs'>""
    + ""<button type='button' class='nav-tab active' data-tab='screens' onclick=\""return openTab('screens')\"">Screens</button>""
    + ""<button type='button' class='nav-tab' data-tab='links' onclick=\""return openTab('links')\"">Dynamic Links</button>""
    + ""<button type='button' class='nav-tab' data-tab='types' onclick=\""return openTab('types')\"">Type Map</button>""
    + ""</div>""
    + ""<div id='screens' class='tab-pane active'><div class='detail-empty'>Loading screens...</div></div>""
    + ""<div id='links' class='tab-pane'><div class='detail-empty'>Open tab to load dynamic links...</div></div>""
    + ""<div id='types' class='tab-pane'><div class='detail-empty'>Open tab to load type map...</div></div>""
    + ""</div>"";
}

function renderModelAccordionTabs(data) {
  return ""<div class='ui-accordion-tabs' id='model-tabs'>""
    + ""<div class='nav-tabs'>""
    + ""<button type='button' class='model-tab active' onclick=\""return openModelTab(this,'model-overview')\"">Variables</button>""
    + ""<button type='button' class='model-tab' onclick=\""return openModelTab(this,'model-datatypes')\"">Type Map</button>""
    + ""</div>""
    + ""<div id='model-overview' class='model-pane active'><div class='detail-empty'>Loading variables...</div></div>""
    + ""<div id='model-datatypes' class='model-pane'><div class='detail-empty'>Open tab to load data types...</div></div>""
    + ""</div>"";
}

function openModelTab(btn, paneId) {
  const container = document.getElementById('model-tabs');
  if (!container) return false;
  container.querySelectorAll('.model-tab').forEach(t => t.classList.remove('active'));
  if (btn) btn.classList.add('active');
  container.querySelectorAll('.model-pane').forEach(p => p.classList.toggle('active', p.id === paneId));
  ensureModelLoaded();
  return false;
}

function renderConvertersAccordionTabs(data) {
  return ""<div class='ui-accordion-tabs' id='converters-tabs'>""
    + ""<div class='nav-tabs'>""
    + ""<button type='button' class='model-tab active' onclick=\""return openConvertersTab(this,'converters-list')\"">Converters</button>""
    + ""<button type='button' class='model-tab' onclick=\""return openConvertersTab(this,'converters-other')\"">Other</button>""
    + ""</div>""
    + ""<div id='converters-list' class='model-pane active'><div class='detail-empty'>Loading converters...</div></div>""
    + ""<div id='converters-other' class='model-pane'><div class='detail-empty'>Open tab to load other definitions...</div></div>""
    + ""</div>"";
}

function openConvertersTab(btn, paneId) {
  const container = document.getElementById('converters-tabs');
  if (!container) return false;
  container.querySelectorAll('.model-tab').forEach(t => t.classList.remove('active'));
  if (btn) btn.classList.add('active');
  container.querySelectorAll('.model-pane').forEach(p => p.classList.toggle('active', p.id === paneId));
  ensureConvertersLoaded();
  return false;
}

function ensureConvertersLoaded() {
  if (convertersState.loaded || convertersState.loading) return;
  const listPane = document.getElementById('converters-list');
  if (!listPane) return;
  convertersState.loading = true;
  loadSectionByKey('converters')
    .then(section => {
      convertersState.loading = false;
      if (!section) {
        listPane.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Converters data unavailable</div><div class='alert-text'>No Converters section was generated for this project.</div></div>"";
        return;
      }
      convertersState.loaded = true;
      convertersState.data = section;
      const listP = document.getElementById('converters-list');
      const otherP = document.getElementById('converters-other');
      if (listP) listP.innerHTML = renderConvertersLazy(section);
      if (otherP) otherP.innerHTML = renderConvertersOtherHtml(section);
    })
    .catch(() => {
      convertersState.loading = false;
      listPane.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load Converters data.</div></div>"";
    });
}

function renderAlarmsAccordionTabs(data) {
  return ""<div class='ui-accordion-tabs' id='alarms-tabs'>""
    + ""<div class='nav-tabs'>""
    + ""<button type='button' class='model-tab active' onclick=\""return openAlarmsTab(this,'alarms-instances')\"">Instances</button>""
    + ""<button type='button' class='model-tab' onclick=\""return openAlarmsTab(this,'alarms-types')\"">Types</button>""
    + ""</div>""
    + ""<div id='alarms-instances' class='model-pane active'><div class='detail-empty'>Loading alarms...</div></div>""
    + ""<div id='alarms-types' class='model-pane'><div class='detail-empty'>Open tab to load alarm types...</div></div>""
    + ""</div>"";
}

function openAlarmsTab(btn, paneId) {
  const container = document.getElementById('alarms-tabs');
  if (!container) return false;
  container.querySelectorAll('.model-tab').forEach(t => t.classList.remove('active'));
  if (btn) btn.classList.add('active');
  container.querySelectorAll('.model-pane').forEach(p => p.classList.toggle('active', p.id === paneId));
  ensureAlarmsLoaded();
  return false;
}

function ensureAlarmsLoaded() {
  if (alarmsState.loaded || alarmsState.loading) return;
  const instancesPane = document.getElementById('alarms-instances');
  if (!instancesPane) return;
  alarmsState.loading = true;
  loadSectionByKey('alarms')
    .then(section => {
      alarmsState.loading = false;
      if (!section) {
        instancesPane.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Alarms data unavailable</div><div class='alert-text'>No Alarms section was generated for this project.</div></div>"";
        return;
      }
      alarmsState.loaded = true;
      alarmsState.data = section;
      const instancesP = document.getElementById('alarms-instances');
      const typesP = document.getElementById('alarms-types');
      if (instancesP) instancesP.innerHTML = renderAlarmsInstancesHtml(section);
      if (typesP) typesP.innerHTML = renderAlarmsTypesHtml(section);
    })
    .catch(() => {
      alarmsState.loading = false;
      instancesPane.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load Alarms data.</div></div>"";
    });
}

function renderAlarmSourceLinkFlag(link) {
  const kind = String((link && link.resultKind) || 'ok');
  if (kind === 'broken') return "" <span class='mini-badge mini-badge-broken'>broken</span>"";
  if (kind === 'unresolved') return "" <span class='mini-badge mini-badge-unresolved'>unresolved</span>"";
  return """";
}

function renderAlarmsInstancesHtml(section) {
  const s = section || {};
  const items = Array.isArray(s.items) ? s.items : [];
  if (!items.length) return ""<div class='detail-empty'>No alarm instances defined under the Alarms folder.</div>"";

  const cards = items.map(a => {
    const links = Array.isArray(a.sourceLinks) ? a.sourceLinks : [];
    const incoming = Array.isArray(a.incomingRefs) ? a.incomingRefs : [];
    const linksSection = links.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Field</th><th>Source</th></tr></thead><tbody>""
        + links.map(l => ""<tr><td>"" + escapeHtml(l.field || '-') + ""</td><td class='mono' title='"" + escapeHtml(l.target || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(l.target || '-', 110)) + renderAlarmSourceLinkFlag(l) + ""</td></tr>"").join('')
        + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No dynamic link bindings found for this alarm.</div>"";
    const incomingSection = incoming.length
      ? ""<ul class='detail-list'>"" + incoming.slice(0, 100).map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 120)) + ""</li>"").join('') + ""</ul>""
      : ""<div class='detail-empty'>Not referenced by any other node.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(a.name) + ""</div><div class='item-meta' title='"" + escapeHtml(a.path || '') + ""'>"" + escapeHtml(a.type || 'Alarm') + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Sources</span><span class='metric-value'>"" + formatNumber(links.length) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section'><h4>Alarm</h4><div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Severity</span><strong>"" + escapeHtml(a.severity || '-') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Message</span><strong>"" + escapeHtml(a.message || '-') + ""</strong></div>""
      + ""</div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Source Bindings ("" + formatNumber(links.length) + "")</h4>"" + linksSection + ""</div>""
      + ""<div class='detail-section detail-section-wide'><h4>Referenced by ("" + formatNumber(incoming.length) + "")</h4>"" + incomingSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Alarm Instances ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function renderAlarmsTypesHtml(section) {
  const s = section || {};
  const items = Array.isArray(s.typeItems) ? s.typeItems : [];
  if (!items.length) return ""<div class='detail-empty'>No custom alarm types defined under the Alarms folder.</div>"";

  const cards = items.map(t => {
    const usagePaths = Array.isArray(t.usagePaths) ? t.usagePaths : [];
    const usageSection = usagePaths.length
      ? ""<ul class='detail-list'>"" + usagePaths.map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 130)) + ""</li>"").join('') + ""</ul>""
        + (Number(t.usageCount) > usagePaths.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(usagePaths.length) + "" of "" + formatNumber(t.usageCount) + "" instances.</div>"" : """")
      : ""<div class='detail-empty'>No instances of this alarm type found.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(t.name) + ""</div><div class='item-meta' title='"" + escapeHtml(t.path || '') + ""'>Extends "" + escapeHtml(t.type || '-') + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Instances</span><span class='metric-value'>"" + formatNumber(t.usageCount) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Instances ("" + formatNumber(t.usageCount) + "")</h4>"" + usageSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Alarm Types ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function renderGenericRefCatalog(items, kindLabel, emptyText) {
  const list = Array.isArray(items) ? items : [];
  if (!list.length) return ""<div class='detail-empty'>"" + escapeHtml(emptyText) + ""</div>"";

  const cards = list.map(c => {
    const usagePaths = Array.isArray(c.usagePaths) ? c.usagePaths : [];
    const refSection = usagePaths.length
      ? ""<ul class='detail-list'>"" + usagePaths.map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 130)) + ""</li>"").join('') + ""</ul>""
        + (Number(c.usageCount) > usagePaths.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(usagePaths.length) + "" of "" + formatNumber(c.usageCount) + "" references.</div>"" : """")
      : ""<div class='detail-empty'>Not referenced by any other node.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(c.name) + ""</div><div class='item-meta' title='"" + escapeHtml(c.path || '') + ""'>"" + escapeHtml(c.kind || kindLabel) + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Referenced by</span><span class='metric-value'>"" + formatNumber(c.usageCount) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Referenced by ("" + formatNumber(c.usageCount) + "")</h4>"" + refSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>"" + escapeHtml(kindLabel) + ""s ("" + formatNumber(list.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function ensureSimpleSectionLoaded(sectionKey, state, hostId, renderFn) {
  if (state.loaded || state.loading) return;
  const host = document.getElementById(hostId);
  if (!host) return;
  state.loading = true;
  loadSectionByKey(sectionKey)
    .then(section => {
      state.loading = false;
      if (!section) {
        host.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Data unavailable</div><div class='alert-text'>No "" + escapeHtml(getCategoryLabel(sectionKey)) + "" section was generated for this project.</div></div>"";
        return;
      }
      state.loaded = true;
      state.data = section;
      host.innerHTML = renderFn(section);
    })
    .catch(() => {
      state.loading = false;
      host.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load "" + escapeHtml(getCategoryLabel(sectionKey)) + "" data.</div></div>"";
    });
}

function renderSimplePanelHost(hostId, loadingText) {
  return ""<div id='"" + hostId + ""'><div class='detail-empty'>"" + escapeHtml(loadingText || 'Loading...') + ""</div></div>"";
}

function renderRecipesHtml(section) {
  const items = Array.isArray(section && section.items) ? section.items : [];
  if (!items.length) return ""<div class='detail-empty'>No recipe schemas defined under the Recipes folder.</div>"";

  const cards = items.map(r => {
    const flags = Array.isArray(r.metadata) ? r.metadata : [];
    const incoming = Array.isArray(r.incomingRefs) ? r.incomingRefs : [];
    const flagsHtml = flags.length ? flags.map(f => ""<span class='mini-badge'>"" + escapeHtml(f) + ""</span>"").join(' ') : ""<span class='detail-empty'>none</span>"";
    const incomingSection = incoming.length
      ? ""<ul class='detail-list'>"" + incoming.slice(0, 100).map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 120)) + ""</li>"").join('') + ""</ul>""
      : ""<div class='detail-empty'>Not referenced by any other node.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(r.name) + ""</div><div class='item-meta' title='"" + escapeHtml(r.path || '') + ""'>RecipeSchema"" + (r.status ? (' · ' + escapeHtml(r.status)) : '') + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Referenced by</span><span class='metric-value'>"" + formatNumber(incoming.length) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section'><h4>Schema</h4><div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Target Node</span><strong class='mono' title='"" + escapeHtml(r.targetNode || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(r.targetNode || '-', 90)) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Store</span><strong class='mono' title='"" + escapeHtml(r.store || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(r.store || '-', 90)) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Metadata</span><strong>"" + flagsHtml + ""</strong></div>""
      + ""</div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Referenced by ("" + formatNumber(incoming.length) + "")</h4>"" + incomingSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Recipe Schemas ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function ensureRecipesLoaded() { ensureSimpleSectionLoaded('recipes', recipesState, 'recipes-host', renderRecipesHtml); }

function renderDataStoresHtml(section) {
  const items = Array.isArray(section && section.items) ? section.items : [];
  if (!items.length) return ""<div class='detail-empty'>No data stores defined under the DataStores folder.</div>"";

  const cards = items.map(d => {
    const tables = Array.isArray(d.tables) ? d.tables : [];
    const incoming = Array.isArray(d.incomingRefs) ? d.incomingRefs : [];
    const tablesSection = tables.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Table</th><th>Columns</th></tr></thead><tbody>""
        + tables.map(t => ""<tr><td>"" + escapeHtml(t.name) + ""</td><td>"" + formatNumber(t.columns || 0) + ""</td></tr>"").join('')
        + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No tables defined.</div>"";
    const incomingSection = incoming.length
      ? ""<ul class='detail-list'>"" + incoming.slice(0, 100).map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 120)) + ""</li>"").join('') + ""</ul>""
      : ""<div class='detail-empty'>Not referenced by any Recipe/Logger Store pointer.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(d.name) + ""</div><div class='item-meta' title='"" + escapeHtml(d.path || '') + ""'>"" + escapeHtml(d.type || 'Store') + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Tables</span><span class='metric-value'>"" + formatNumber(tables.length) + ""</span></div>""
      + ""<div class='metric-badge'><span class='metric-label'>Used by</span><span class='metric-value'>"" + formatNumber(incoming.length) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Tables ("" + formatNumber(tables.length) + "")</h4>"" + tablesSection + ""</div>""
      + ""<div class='detail-section detail-section-wide'><h4>Used by ("" + formatNumber(incoming.length) + "")</h4>"" + incomingSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Data Stores ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function ensureDataStoresLoaded() { ensureSimpleSectionLoaded('datastores', dataStoresState, 'datastores-host', renderDataStoresHtml); }

function renderLoggersHtml(section) {
  const items = Array.isArray(section && section.items) ? section.items : [];
  if (!items.length) return ""<div class='detail-empty'>No data loggers defined under the Loggers folder.</div>"";

  const cards = items.map(l => {
    const vars = Array.isArray(l.variables) ? l.variables : [];
    const incoming = Array.isArray(l.incomingRefs) ? l.incomingRefs : [];
    const varsSection = vars.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Variable</th><th>Source</th></tr></thead><tbody>""
        + vars.map(v => ""<tr><td>"" + escapeHtml(String(v.field || '').replace(/^VariablesToLog\//, '').replace(/\/DynamicLink$/, '')) + ""</td><td class='mono' title='"" + escapeHtml(v.target || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(v.target || '-', 100)) + renderAlarmSourceLinkFlag(v) + ""</td></tr>"").join('')
        + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No variables configured for logging.</div>"";
    const incomingSection = incoming.length
      ? ""<ul class='detail-list'>"" + incoming.slice(0, 100).map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 120)) + ""</li>"").join('') + ""</ul>""
      : ""<div class='detail-empty'>Not referenced by any other node.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(l.name) + ""</div><div class='item-meta' title='"" + escapeHtml(l.path || '') + ""'>Logger</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Variables</span><span class='metric-value'>"" + formatNumber(vars.length) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section'><h4>Logger</h4><div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Store</span><strong class='mono' title='"" + escapeHtml(l.store || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(l.store || '-', 90)) + ""</strong></div>""
      + ""</div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Logged Variables ("" + formatNumber(vars.length) + "")</h4>"" + varsSection + ""</div>""
      + ""<div class='detail-section detail-section-wide'><h4>Referenced by ("" + formatNumber(incoming.length) + "")</h4>"" + incomingSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Data Loggers ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function ensureLoggersLoaded() { ensureSimpleSectionLoaded('loggers', loggersState, 'loggers-host', renderLoggersHtml); }

function renderTranslationsHtml(section) {
  const items = Array.isArray(section && section.items) ? section.items : [];
  if (!items.length) return ""<div class='detail-empty'>No localization dictionaries defined under the Translations folder.</div>"";

  const cards = items.map(d => {
    const localeStats = Array.isArray(d.localeStats) ? d.localeStats : [];
    const statsRows = localeStats.length
      ? ""<div class='table-scroll'><table class='table-compact'><thead><tr><th>Locale</th><th>Translated</th><th>Missing</th></tr></thead><tbody>""
        + localeStats.map(s => ""<tr><td>"" + escapeHtml(s.locale) + ""</td><td>"" + formatNumber(s.translated) + ""</td><td>"" + formatNumber(s.missing) + ""</td></tr>"").join('')
        + ""</tbody></table></div>""
      : ""<div class='detail-empty'>No locale columns found.</div>"";
    const worst = localeStats.slice().sort((a, b) => Number(b.missing || 0) - Number(a.missing || 0))[0];
    const missingSection = (worst && Array.isArray(worst.missingKeys) && worst.missingKeys.length)
      ? ""<div class='detail-section detail-section-wide'><h4>Missing in "" + escapeHtml(worst.locale) + "" ("" + formatNumber(worst.missing) + "")</h4>""
        + ""<ul class='detail-list'>"" + worst.missingKeys.map(k => ""<li>"" + escapeHtml(k) + ""</li>"").join('') + ""</ul>""
        + ""</div>""
      : """";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(d.name) + ""</div><div class='item-meta'>"" + formatNumber(d.keyCount) + "" keys · "" + formatNumber((d.locales || []).length) + "" locales</div></div>""
      + ""<div class='item-right'>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Locale Completion</h4>"" + statsRows + ""</div>""
      + missingSection
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Localization Dictionaries ("" + formatNumber(items.length) + "")</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function ensureTranslationsLoaded() { ensureSimpleSectionLoaded('translations', translationsState, 'translations-host', renderTranslationsHtml); }

function ensureReportsLoaded() {
  ensureSimpleSectionLoaded('reports', reportsState, 'reports-host', s => renderGenericRefCatalog(s && s.items, 'Node', 'No report items found under the Reports folder.'));
}

function ensureRetentivityLoaded() {
  ensureSimpleSectionLoaded('retentivity', retentivityState, 'retentivity-host', s => renderGenericRefCatalog(s && s.items, 'Node', 'No retentivity items found under the Retentivity folder.'));
}

function ensureMqttLoaded() { ensureSimpleSectionLoaded('mqtt', mqttState, 'mqtt-host', renderMqttHtml); }

function ensureNetLogicLoaded() {
  if (netlogicState.loaded || netlogicState.loading) return;
  const host = document.getElementById('netlogic-host');
  if (!host) return;
  netlogicState.loading = true;
  loadSectionByKey('netlogic')
    .then(section => {
      netlogicState.loading = false;
      if (!section) {
        host.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Data unavailable</div><div class='alert-text'>No NetLogic section was generated for this project.</div></div>"";
        return;
      }
      netlogicState.loaded = true;
      netlogicState.data = section;
      host.innerHTML = renderNetLogicLazy(section);
      initNetLogicTypesControls(section);
    })
    .catch(() => {
      netlogicState.loading = false;
      host.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load NetLogic data.</div></div>"";
    });
}

function ensureOpcUaLoaded() {
  if (opcuaState.loaded || opcuaState.loading) return;
  const host = document.getElementById('opcua-host');
  if (!host) return;
  opcuaState.loading = true;
  loadSectionByKey('opcua')
    .then(section => {
      opcuaState.loading = false;
      if (!section) {
        host.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Data unavailable</div><div class='alert-text'>No OPC-UA section was generated for this project.</div></div>"";
        return;
      }
      opcuaState.loaded = true;
      opcuaState.data = section;
      host.innerHTML = renderOpcUaSection(section);
      initOpcUaEntityControls(section);
    })
    .catch(() => {
      opcuaState.loading = false;
      host.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load OPC-UA data.</div></div>"";
    });
}

function renderMqttHtml(section) {
  const broker = section && section.broker;
  const clients = Array.isArray(section && section.clients) ? section.clients : [];
  const hasData = !!broker || clients.length > 0;
  if (!hasData) return ""<div class='detail-empty'>No MQTT broker or client configuration found under the MQTT folder.</div>"";

  const brokerHtml = broker
    ? ""<div class='detail-section'><h4>Broker</h4><div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Name</span><strong class='mono' title='"" + escapeHtml(broker.path || '') + ""'>"" + escapeHtml(broker.name || '-') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Listener Port</span><strong>"" + formatNumber(broker.listenerPort) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Max Connections</span><strong>"" + formatNumber(broker.maxConnections) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Retain Available</span><strong>"" + (broker.retainAvailable ? 'Yes' : 'No') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Data Persistence</span><strong>"" + (broker.dataPersistence ? 'Yes' : 'No') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Anonymous Auth</span><strong>"" + (broker.anonymousAuthentication ? 'Yes' : 'No') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Running</span><strong>"" + (broker.running ? 'Yes' : 'No') + ""</strong></div>""
      + ""</div></div>""
    : ""<div class='detail-section'><h4>Broker</h4><div class='detail-empty'>No MQTT broker configured.</div></div>"";

  const clientCards = clients.map(c => {
    const publishers = Array.isArray(c.publishers) ? c.publishers : [];
    const subscribers = Array.isArray(c.subscribers) ? c.subscribers : [];

    const publishersHtml = publishers.length
      ? publishers.map(p => ""<div class='detail-kv-row'><span>"" + escapeHtml(p.name) + "" &rarr; <span class='mono'>"" + escapeHtml(p.topic || '-') + ""</span></span><strong class='mono' title='"" + escapeHtml(p.dataSourcePath || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(p.dataSourcePath || '-', 80)) + ""</strong></div>""
          + (Array.isArray(p.variables) && p.variables.length
            ? ""<div class='mono' style='padding:2px 0 6px;word-break:break-word;'>"" + p.variables.slice(0, 50).map(v => escapeHtml(v)).join(', ') + ""</div>""
            : """")
        ).join('')
      : ""<div class='detail-empty'>No publishers configured.</div>"";

    const subscribersHtml = subscribers.length
      ? subscribers.map(s => {
          const subs = Array.isArray(s.subscriptions) ? s.subscriptions : [];
          return ""<div class='detail-kv-row'><span><strong>"" + escapeHtml(s.name) + ""</strong></span></div>""
            + subs.map(sub => ""<div class='detail-kv-row'><span>&nbsp;&nbsp;"" + escapeHtml(sub.topic || '-') + ""</span><strong class='mono' title='"" + escapeHtml(sub.targetPath || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(sub.targetPath || '-', 80)) + ""</strong></div>""
              + (Array.isArray(sub.variables) && sub.variables.length
                ? ""<div class='mono' style='padding:2px 0 6px;word-break:break-word;'>"" + sub.variables.slice(0, 50).map(v => escapeHtml(v)).join(', ') + ""</div>""
                : """")
            ).join('');
        }).join('')
      : ""<div class='detail-empty'>No subscribers configured.</div>"";

    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(c.name) + ""</div><div class='item-meta' title='"" + escapeHtml(c.path || '') + ""'>MQTT Client &bull; "" + escapeHtml(c.brokerAddress || '-') + "":"" + formatNumber(c.brokerPort) + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Publishers</span><span class='metric-value'>"" + formatNumber(publishers.length) + ""</span></div>""
      + ""<div class='metric-badge'><span class='metric-label'>Subscribers</span><span class='metric-value'>"" + formatNumber(subscribers.length) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section'><h4>Connection</h4><div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Client Id</span><strong class='mono'>"" + escapeHtml(c.clientId || '-') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>SSL/TLS</span><strong>"" + (c.sslEnabled ? 'Yes' : 'No') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Status</span><strong>"" + escapeHtml(c.status || '-') + ""</strong></div>""
      + ""</div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Publishers ("" + formatNumber(publishers.length) + "")</h4><div class='detail-kv detail-kv-compact'>"" + publishersHtml + ""</div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Subscribers ("" + formatNumber(subscribers.length) + "")</h4><div class='detail-kv detail-kv-compact'>"" + subscribersHtml + ""</div></div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return brokerHtml
    + ""<div class='detail-section detail-section-wide'><h4>Clients ("" + formatNumber(clients.length) + "")</h4>""
    + (clients.length ? ""<div class='item-list'><div class='item-list-scroll'>"" + clientCards + ""</div></div>"" : ""<div class='detail-empty'>No MQTT clients configured.</div>"")
    + ""</div>"";
}

function renderSecurityAccordionTabs(data) {
  return ""<div class='ui-accordion-tabs' id='security-tabs'>""
    + ""<div class='nav-tabs'>""
    + ""<button type='button' class='model-tab active' onclick=\""return openSecurityTab(this,'security-users')\"">Users</button>""
    + ""<button type='button' class='model-tab' onclick=\""return openSecurityTab(this,'security-groups')\"">Groups</button>""
    + ""<button type='button' class='model-tab' onclick=\""return openSecurityTab(this,'security-roles')\"">Roles</button>""
    + ""</div>""
    + ""<div id='security-users' class='model-pane active'><div class='detail-empty'>Loading users...</div></div>""
    + ""<div id='security-groups' class='model-pane'><div class='detail-empty'>Open tab to load groups...</div></div>""
    + ""<div id='security-roles' class='model-pane'><div class='detail-empty'>Open tab to load roles...</div></div>""
    + ""</div>"";
}

function openSecurityTab(btn, paneId) {
  const container = document.getElementById('security-tabs');
  if (!container) return false;
  container.querySelectorAll('.model-tab').forEach(t => t.classList.remove('active'));
  if (btn) btn.classList.add('active');
  container.querySelectorAll('.model-pane').forEach(p => p.classList.toggle('active', p.id === paneId));
  ensureSecurityLoaded();
  return false;
}

function ensureSecurityLoaded() {
  if (securityState.loaded || securityState.loading) return;
  const usersPane = document.getElementById('security-users');
  if (!usersPane) return;
  securityState.loading = true;
  loadSectionByKey('security')
    .then(section => {
      securityState.loading = false;
      if (!section) {
        usersPane.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Security data unavailable</div><div class='alert-text'>No Security section was generated for this project.</div></div>"";
        return;
      }
      securityState.loaded = true;
      securityState.data = section;
      const usersP = document.getElementById('security-users');
      const groupsP = document.getElementById('security-groups');
      const rolesP = document.getElementById('security-roles');
      if (usersP) usersP.innerHTML = renderGenericRefCatalog(section.users, 'User', 'No users configured under Security/Users.');
      if (groupsP) groupsP.innerHTML = renderGenericRefCatalog(section.groups, 'Group', 'No groups configured under Security/Groups.');
      if (rolesP) rolesP.innerHTML = renderGenericRefCatalog(section.roles, 'Role', 'No roles configured under Security/Roles.');
    })
    .catch(() => {
      securityState.loading = false;
      usersPane.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load Security data.</div></div>"";
    });
}

function ensureModelLoaded() {
  if (modelState.loaded || modelState.loading) return;
  const overviewPane = document.getElementById('model-overview');
  if (!overviewPane) return;
  modelState.loading = true;
  loadSectionByKey('model')
    .then(section => {
      modelState.loading = false;
      const model = (section && section.model)
        || (reportRuntimeState.payload && reportRuntimeState.payload.model)
        || null;
      if (!model) {
        overviewPane.innerHTML = ""<div class='alert-box warn'><div class='alert-title'>Model data unavailable</div><div class='alert-text'>No Model section was generated for this project.</div></div>"";
        return;
      }
      modelState.loaded = true;
      modelState.data = model;
      const ov = document.getElementById('model-overview');
      const dt = document.getElementById('model-datatypes');
      if (ov) ov.innerHTML = buildModelOverviewHtml(model);
      if (dt) dt.innerHTML = buildModelTypesHtml(model);
    })
    .catch(() => {
      modelState.loading = false;
      overviewPane.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load Model data.</div></div>"";
    });
}

function buildModelOverviewHtml(model) {
  const tree = Array.isArray(model.tree) ? model.tree : [];
  if (!tree.length) return ""<div class='detail-empty'>No model variables found.</div>"";
  return ""<h3>Model Variables</h3><div class='item-list'><div class='item-list-scroll'>""
    + tree.map(renderModelTreeNode).join('')
    + ""</div></div>"";
}

function modelCrossRefSections(node, incoming) {
  const outSection = node.linkTarget
    ? ""<div class='detail-section detail-section-wide'><h4>Links to</h4>""
      + ""<span class='mono' title='"" + escapeHtml(node.linkTarget) + ""'>"" + escapeHtml(abbreviatePathMiddle(node.linkTarget, 100)) + ""</span>"" + renderModelLinkFlag(node)
      + ""</div>""
    : """";
  const inSection = incoming.length
    ? ""<div class='detail-section detail-section-wide'><h4>Referenced by ("" + formatNumber(incoming.length) + "")</h4>""
      + ""<ul class='detail-list'>"" + incoming.slice(0, 100).map(s => ""<li class='mono' title='"" + escapeHtml(s) + ""'>"" + escapeHtml(abbreviatePathMiddle(s, 120)) + ""</li>"").join('') + ""</ul>""
      + (incoming.length > 100 ? ""<div class='chart-subtitle'>Showing 100 of "" + formatNumber(incoming.length) + "" references.</div>"" : """")
      + ""</div>""
    : """";
  return outSection + inSection;
}

function renderModelTreeNode(node) {
  const children = Array.isArray(node.children) ? node.children : [];
  const incoming = Array.isArray(node.incomingRefs) ? node.incomingRefs : [];
  const outBadge = node.linkTarget
    ? (renderModelLinkFlag(node) || "" <span class='mini-badge mini-badge-repeat'>linked</span>"")
    : """";
  const refBadge = incoming.length
    ? ""<div class='metric-badge'><span class='metric-label'>Referenced by</span><span class='metric-value'>"" + formatNumber(incoming.length) + ""</span></div>""
    : """";

  // Container (object / instance / structure): expandable to reveal its members and
  // its own cross-references (e.g. a UI widget bound to the whole object).
  if (node.kind !== 'scalar') {
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(node.name) + ""</div><div class='item-meta'>object</div></div>""
      + ""<div class='item-right'>""
      + outBadge
      + ""<div class='metric-badge'><span class='metric-label'>Members</span><span class='metric-value'>"" + formatNumber(children.length) + ""</span></div>""
      + refBadge
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens model-tree-children'>""
      + modelCrossRefSections(node, incoming)
      + children.map(renderModelTreeNode).join('')
      + ""</div></div>""
      + ""</div>"";
  }

  // Scalar variable: leaf. Expandable only when it has cross-references.
  const hasRefs = !!node.linkTarget || incoming.length > 0;
  const head = ""<div class='item-head'"" + (hasRefs ? "" onclick='toggleExpand(this.closest(\"".item-row\""))'"" : """") + "">""
    + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(node.name) + ""</div><div class='item-meta'>"" + escapeHtml(node.dataType || '-') + ""</div></div>""
    + ""<div class='item-right'>""
    + outBadge
    + ""<div class='metric-badge'><span class='metric-label'>Referenced by</span><span class='metric-value'>"" + formatNumber(incoming.length) + ""</span></div>""
    + (hasRefs ? ""<button class='expand-btn' type='button' aria-label='Expand details'></button>"" : """")
    + ""</div></div>"";

  if (!hasRefs) {
    return ""<div class='item-row item-row-leaf'>"" + head + ""</div>"";
  }
  return ""<div class='item-row'>"" + head
    + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
    + modelCrossRefSections(node, incoming)
    + ""</div></div>""
    + ""</div>"";
}

function buildModelTypesHtml(model) {
  const types = Array.isArray(model.types) ? model.types : [];
  if (!types.length) return ""<div class='detail-empty'>No custom Model types defined.</div>"";

  const cards = types.map(t => {
    const fields = Array.isArray(t.fields) ? t.fields : [];
    const fieldRows = fields.length
      ? fields.map(f => ""<tr><td>"" + escapeHtml(f.name) + ""</td><td class='mono'>"" + escapeHtml(f.type) + ""</td></tr>"").join('')
      : ""<tr><td colspan='2' class='detail-empty'>No fields.</td></tr>"";
    const location = escapeHtml(t.path || '');
    const usagePaths = Array.isArray(t.usagePaths) ? t.usagePaths : [];
    const usageSection = usagePaths.length
      ? ""<ul class='detail-list'>"" + usagePaths.map(p => ""<li class='mono' title='"" + escapeHtml(p) + ""'>"" + escapeHtml(abbreviatePathMiddle(p, 130)) + ""</li>"").join('') + ""</ul>""
        + (Number(t.usageCount) > usagePaths.length ? ""<div class='chart-subtitle'>Showing "" + formatNumber(usagePaths.length) + "" of "" + formatNumber(t.usageCount) + "" usages.</div>"" : """")
      : ""<div class='detail-empty'>Not used anywhere in the project.</div>"";
    return ""<div class='item-row'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(t.name) + ""</div><div class='item-meta' title='"" + location + ""'>"" + location + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Fields</span><span class='metric-value'>"" + formatNumber(t.fieldCount) + ""</span></div>""
      + ""<div class='metric-badge'><span class='metric-label'>Usages</span><span class='metric-value'>"" + formatNumber(t.usageCount) + ""</span></div>""
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section'><h4>Type</h4><div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Extends</span><strong>"" + escapeHtml(t.superType || '-') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Location</span><strong>"" + escapeHtml(t.path || '-') + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Fields</span><strong>"" + formatNumber(t.fieldCount) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Usages</span><strong>"" + formatNumber(t.usageCount) + ""</strong></div>""
      + ""</div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Fields</h4><div class='table-scroll'><table class='table-compact'><thead><tr><th>Name</th><th>Type</th></tr></thead><tbody>""
      + fieldRows
      + ""</tbody></table></div></div>""
      + ""<div class='detail-section detail-section-wide'><h4>Used in ("" + formatNumber(t.usageCount) + "")</h4>"" + usageSection + ""</div>""
      + ""</div></div>""
      + ""</div>"";
  }).join('');

  return ""<h3>Model Types</h3><div class='item-list'><div class='item-list-scroll'>"" + cards + ""</div></div>"";
}

function renderModelLinkFlag(v) {
  const kind = String((v && v.linkKind) || 'ok');
  if (kind === 'broken') return "" <span class='mini-badge mini-badge-broken'>broken</span>"";
  if (kind === 'unresolved') return "" <span class='mini-badge mini-badge-unresolved'>unresolved</span>"";
  if (v && v.isComplex) return "" <span class='mini-badge mini-badge-complex'>complex</span>"";
  return """";
}

function attentionFromFindings(findings) {
  if (!Array.isArray(findings) || !findings.length) return 'low';
  if (findings.some(f => f.severity === 'high')) return 'high';
  if (findings.some(f => f.severity === 'medium')) return 'medium';
  return 'low';
}

function renderCategoryFindings(findings) {
  if (!Array.isArray(findings) || !findings.length) return """";
  const items = findings.map(f => {
    const evidence = Array.isArray(f.evidence) ? f.evidence : [];
    const rows = evidence.map(e =>
      ""<div class='evidence-row'><span class='mono' title='"" + escapeHtml(e.path || '') + ""'>"" + escapeHtml(abbreviatePathMiddle(e.path || '', 110)) + ""</span>""
      + (e.target ? ""<span class='evidence-target' title='"" + escapeHtml(String(e.target)) + ""'>"" + escapeHtml(abbreviatePathMiddle(String(e.target), 70)) + ""</span>"" : """")
      + ""</div>""
    ).join('') || ""<div class='detail-empty'>No details.</div>"";
    return ""<div class='finding finding--"" + escapeHtml(f.severity || 'low') + ""'>""
      + ""<button type='button' class='finding-head' onclick='toggleFinding(this)'>""
      + ""<span class='finding-dot'></span>""
      + ""<span class='finding-title'>"" + escapeHtml(f.title || '') + ""</span>""
      + ""<span class='finding-detail'>"" + escapeHtml(f.detail || '') + ""</span>""
      + ""<span class='finding-chevron'>\u25be</span>""
      + ""</button>""
      + ""<div class='finding-evidence'>"" + rows + ""</div>""
      + ""</div>"";
  }).join('');
  return ""<div class='findings'>"" + items + ""</div>"";
}

function toggleFinding(btn) {
  const finding = btn.closest('.finding');
  if (finding) finding.classList.toggle('open');
}

function buildCategoryStrip(key, data) {
  const ov = data.overview || {};
  const ss = data.screenStats || {};
  const cm = (data.categoryMetrics && data.categoryMetrics[key]) || {};
  const ms = data.modelSummary || {};
  const special = data.specialMetrics || {};
  const solution = data.solutionInsights || {};
  const findings = (data.findings && data.findings[key]) || [];
  const linkLevel = attentionFromFindings(findings);
  const n = v => formatNumber(v || 0);
  const avg = v => Number(v || 0).toFixed(1);

  if (key === 'ui') {
    return [
      { label: 'Screens', value: n(ov.screensCount) },
      { label: 'Nodes / Screen', inline: [{ k: 'Max', v: n(ss.nodeMax) }, { k: 'Min', v: n(ss.nodeMin) }, { k: 'Avg', v: avg(ss.nodeAvg) }] },
      { label: 'Depth / Screen', inline: [{ k: 'Max', v: n(ss.depthMax) }, { k: 'Min', v: n(ss.depthMin) }, { k: 'Avg', v: avg(ss.depthAvg) }] },
      { label: 'Dynamic Links', value: n(ov.totalDynamicLinks), level: linkLevel, drill: 'links' }
    ];
  }
  if (key === 'model') {
    return [
      { label: 'Nodes', value: n(cm.nodes) },
      { label: 'Objects', value: n(cm.objects) },
      { label: 'Variables', value: n(cm.variables) },
      { label: 'Custom Types', value: n(ms.typeCount) }
    ];
  }
  if (key === 'netlogic') {
    const netlogic = special.netlogic || {};
    const strip = [
      { label: 'Instances', value: n(netlogic.total) },
      { label: 'NetLogic Types', value: n(netlogic.uniqueTypes) },
      { label: 'Most Repeated Type', value: n(netlogic.mostRepeatedTypeInstances) }
    ];
    if (Number(netlogic.runtime) > 0) strip.push({ label: 'Used Outside NetLogic Folder', value: n(netlogic.runtime) });
    if (Number(solution.netLogicFiles) > 0) strip.push({ label: 'Source Files', value: n(solution.netLogicFiles) });
    return strip;
  }
  if (key === 'opcua') {
    const opcua = special.opcua || {};
    const strip = [
      { label: 'Clients', value: n(opcua.clients) },
      { label: 'Servers', value: n(opcua.servers) },
      { label: 'Subscriptions', value: n(opcua.subscriptions) }
    ];
    if (Number(opcua.subscriptionsWithoutClient) > 0) {
      strip.push({ label: 'Subscriptions Without Client', value: n(opcua.subscriptionsWithoutClient), level: 'medium', drill: 'opcua' });
    }
    return strip;
  }
  if (key === 'commdrivers') {
    const commdrivers = special.commdrivers || {};
    const strip = [
      { label: 'Drivers', value: n(commdrivers.drivers) },
      { label: 'Tags', value: n(commdrivers.tags) },
      { label: 'Tags With Incoming Dynamic-Link Ref', value: n(commdrivers.tagsWithIncomingRef) }
    ];
    if (Number(commdrivers.tagsWithoutIncomingRef) > 0) {
      strip.push({ label: 'Tags Without Detected Incoming Dynamic-Link References', value: n(commdrivers.tagsWithoutIncomingRef), level: 'medium', drill: 'commdrivers' });
    }
    if (Number(commdrivers.unresolvedCandidateCount) > 0) {
      strip.push({ label: 'Unresolved CommDrivers Dynamic-Link Candidates', value: n(commdrivers.unresolvedCandidateCount), level: 'medium', drill: 'commdrivers' });
    }
    if (Number(commdrivers.driversWithoutTags) > 0) {
      strip.push({ label: 'Drivers Without Tags', value: n(commdrivers.driversWithoutTags), level: 'medium', drill: 'commdrivers' });
    }
    return strip;
  }
  const strip = [{ label: 'Nodes', value: n(cm.nodes) }];
  if (Number(cm.objects) > 0) strip.push({ label: 'Objects', value: n(cm.objects) });
  if (Number(cm.variables) > 0) strip.push({ label: 'Variables', value: n(cm.variables) });
  if (Number(cm.dynamicLinks) > 0 || findings.length) strip.push({ label: 'Dynamic Links', value: n(cm.dynamicLinks), level: linkLevel, drill: 'links' });
  if (Number(cm.netLogic) > 0) strip.push({ label: 'NetLogic', value: n(cm.netLogic) });
  return strip;
}

function renderCategoryStrip(key, strip) {
  const cards = strip.map(s => {
    const lvlClass = (s.level === 'medium' || s.level === 'high') ? ' lvl-' + s.level : '';
    const clickable = s.drill ? ' clickable' : '';
    const onclick = s.drill ? "" onclick=\""openCategoryDrill('"" + escapeHtml(key) + ""','"" + escapeHtml(s.drill) + ""')\"""" : """";
    const valHtml = s.inline
      ? ""<div class='value-inline'>"" + s.inline.map(p => ""<span class='vi-k'>"" + escapeHtml(p.k) + ""</span> "" + escapeHtml(p.v)).join(""<span class='vi-sep'>\u00b7</span>"") + ""</div>""
      : ""<div class='value'>"" + escapeHtml(s.value) + ""</div>"";
    return ""<div class='summary-card"" + lvlClass + clickable + ""'"" + onclick + ""><div class='label'>"" + escapeHtml(s.label) + ""</div>"" + valHtml + ""</div>"";
  }).join('');
  return ""<div class='project-category-stats'><div class='summary'>"" + cards + ""</div></div>"";
}

function openCategoryDrill(key, drill) {
  const card = document.getElementById('category-card-' + key);
  const body = document.getElementById('category-body-' + key);
  if (card) card.classList.add('open');
  if (body) body.style.display = '';
  if (key === 'model') { ensureModelLoaded(); }
  if (key === 'converters') { ensureConvertersLoaded(); }
  if (key === 'alarms') { ensureAlarmsLoaded(); }
  if (key === 'recipes') { ensureRecipesLoaded(); }
  if (key === 'datastores') { ensureDataStoresLoaded(); }
  if (key === 'loggers') { ensureLoggersLoaded(); }
  if (key === 'translations') { ensureTranslationsLoaded(); }
  if (key === 'security') { ensureSecurityLoaded(); }
  if (key === 'reports') { ensureReportsLoaded(); }
  if (key === 'retentivity') { ensureRetentivityLoaded(); }
  if (key === 'mqtt') { ensureMqttLoaded(); }
  if (key === 'commdrivers') { ensureCommDriversLoaded(); }
  if (key === 'netlogic') { ensureNetLogicLoaded(); }
  if (key === 'opcua') { ensureOpcUaLoaded(); }
  if (key === 'ui') { openTab(drill === 'links' ? 'links' : 'screens'); return; }
  const host = document.getElementById('category-findings-' + key);
  if (host) {
    const findings = (reportRuntimeState.payload && reportRuntimeState.payload.findings && reportRuntimeState.payload.findings[key]) || [];
    host.innerHTML = renderCategoryFindings(findings) || ""<div class='detail-empty'>No issues found.</div>"";
    if (host.scrollIntoView) host.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
  }
}

function renderCategoryPreview(key, cards) {
  const pills = cards.map(s => {
    const lvl = (s.level === 'high') ? 'high' : 'medium';
    const drill = s.drill ? s.drill : '';
    const val = s.inline
      ? s.inline.map(p => p.k + ' ' + p.v).join(' \u00b7 ')
      : s.value;
    return ""<button type='button' class='cat-preview-pill lvl-"" + lvl + ""' onclick=\""event.stopPropagation(); openCategoryDrill('"" + escapeHtml(key) + ""','"" + escapeHtml(drill) + ""')\"">""
      + ""<span class='cpp-dot'></span>""
      + ""<span class='cpp-k'>"" + escapeHtml(s.label) + ""</span>""
      + ""<span class='cpp-v'>"" + escapeHtml(val) + ""</span>""
      + ""</button>"";
  }).join('');
  return ""<div class='project-category-preview' onclick=\""toggleCategoryCard('"" + escapeHtml(key) + ""')\"">"" + pills + ""</div>"";
}

function renderProjectCategoriesPanel(data) {
  const categoryDefs = getProjectCategoryDefs(data);

  const cards = categoryDefs.map((def) => {
    const strip = buildCategoryStrip(def.key, data);
    const previewCards = strip.filter(s => s.level === 'medium' || s.level === 'high');
    const previewHtml = renderCategoryPreview(def.key, previewCards);
    const findings = (data.findings && data.findings[def.key]) || [];
    const attnLevel = attentionFromFindings(findings);
    const sevClass = (attnLevel === 'medium' || attnLevel === 'high') ? ' sev-' + attnLevel : '';

    // CommDrivers no longer uses the generic lazy/CSV/Load-Details path (it has its own
    // auto-loading host below, same as NetLogic/OPC-UA); isLazy is currently always false.
    const isLazy = false;
    const actions = ""<div class='category-actions'><button class='chart-toggle' type='button' onclick=\""downloadCategoryMetricsCsv('"" + escapeHtml(def.key) + ""')\"">Export Category CSV</button>""
      + (isLazy ? ""<button class='chart-toggle' type='button' onclick=\""ensureCategoryLazyLoaded('"" + escapeHtml(def.key) + ""')\"">Load Details</button>"" : """")
      + ""</div>"";
    const drillBody = def.key === 'ui'
      ? renderUiAccordionTabs(data)
      : def.key === 'model'
        ? renderModelAccordionTabs(data)
        : def.key === 'converters'
          ? renderConvertersAccordionTabs(data)
          : def.key === 'alarms'
            ? renderAlarmsAccordionTabs(data)
            : def.key === 'recipes'
              ? renderSimplePanelHost('recipes-host', 'Loading recipes...')
              : def.key === 'datastores'
                ? renderSimplePanelHost('datastores-host', 'Loading data stores...')
                : def.key === 'loggers'
                  ? renderSimplePanelHost('loggers-host', 'Loading loggers...')
                  : def.key === 'translations'
                    ? renderSimplePanelHost('translations-host', 'Loading translations...')
                    : def.key === 'security'
                      ? renderSecurityAccordionTabs(data)
                      : def.key === 'reports'
                        ? renderSimplePanelHost('reports-host', 'Loading reports...')
                        : def.key === 'retentivity'
                          ? renderSimplePanelHost('retentivity-host', 'Loading retentivity...')
                          : def.key === 'mqtt'
                            ? renderSimplePanelHost('mqtt-host', 'Loading MQTT...')
                            : def.key === 'netlogic'
                              ? renderSimplePanelHost('netlogic-host', 'Loading NetLogic types...')
                              : def.key === 'opcua'
                                ? renderSimplePanelHost('opcua-host', 'Loading OPC-UA data...')
                                : def.key === 'commdrivers'
                                  ? renderSimplePanelHost('commdrivers-host', 'Loading CommDrivers data...')
                                  : (() => {
                                const cm = (data.categoryMetrics && data.categoryMetrics[def.key]) || {};
                                // cm.objects counts Folder nodes too (a Folder is also a UAObject),
                                // so an empty category's own top-level folder always makes objects>0.
                                // Require real content beyond the container folders themselves.
                                const hasGenericData = Number(cm.nodes) > Number(cm.folders)
                                  || Number(cm.variables) > 0
                                  || Number(cm.dynamicLinks) > 0
                                  || Number(cm.netLogic) > 0;
                                return hasGenericData
                                  ? actions
                                    + ""<div id='category-findings-"" + escapeHtml(def.key) + ""'></div>""
                                    + (isLazy ? renderCategoryLazyHost(def.key) : """")
                                  : ""<div class='detail-empty'>No items found.</div>"";
                              })();

    return ""<article class='project-category-card"" + sevClass + ""' id='category-card-"" + escapeHtml(def.key) + ""'>""
      + ""<button type='button' class='project-category-head' onclick=\""toggleCategoryCard('"" + escapeHtml(def.key) + ""')\""><h4>"" + escapeHtml(def.label) + ""</h4><span class='project-category-right'><span class='project-category-chevron'>\u25be</span></span></button>""
      + previewHtml
      + ""<div class='project-category-body' id='category-body-"" + escapeHtml(def.key) + ""' style='display:none'>""
      + renderCategoryStrip(def.key, strip)
      + drillBody
      + ""</div>""
      + ""</article>"";
  }).join('');

  return ""<section class='project-categories'>""
    + ""<div class='project-categories-grid'>"" + cards + ""</div>""
    + ""</section>"";
}

function getTypeSeverity(typeItem) {
  const usage = Number(typeItem.usageCount || 0);
  const contentNodes = Number(typeItem.contentNodeCount || 0);
  const internalDeps = Number(typeItem.internalDependencyCount || 0);

  let score = 0;
  if (usage >= 500) score += 3;
  else if (usage >= 200) score += 2;
  else if (usage >= 80) score += 1;

  if (contentNodes >= 300) score += 2;
  else if (contentNodes >= 120) score += 1;

  if (internalDeps >= 20) score += 2;
  else if (internalDeps >= 8) score += 1;

  if (score >= 5) return { label: 'High', className: 'severity-high' };
  if (score >= 3) return { label: 'Medium', className: 'severity-medium' };
  return { label: 'Low', className: 'severity-low' };
}

function sortTypes(items, mode) {
  const rows = items.slice();

  switch (mode) {
    case 'usage-desc':
      rows.sort((a, b) => Number(b.usageCount || 0) - Number(a.usageCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'content-desc':
      rows.sort((a, b) => Number(b.contentNodeCount || 0) - Number(a.contentNodeCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'screens-desc':
      rows.sort((a, b) => Number(b.screenCount || 0) - Number(a.screenCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'name-desc':
      rows.sort((a, b) => String(b.name || '').localeCompare(String(a.name || '')));
      break;
    case 'name-asc':
    default:
      rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')));
      break;
  }

  return rows;
}

function buildTypeMapCsv(rows) {
  const headers = ['name', 'kind', 'namespaceUri', 'usageCount', 'screenCount', 'contentNodeCount', 'contentVariableCount', 'contentDynamicLinkCount', 'internalDependencyCount'];

  function csvEscape(value) {
    const text = String(value == null ? '' : value);
    if (/["",\n\r]/.test(text)) {
      return '""' + text.replace(/""/g, '""""') + '""';
    }
    return text;
  }

  const lines = [headers.join(',')];
  rows.forEach(item => {
    const values = [
      item.name,
      item.kind,
      item.namespaceUri,
      item.usageCount,
      item.screenCount,
      item.contentNodeCount,
      item.contentVariableCount,
      item.contentDynamicLinkCount,
      item.internalDependencyCount
    ];
    lines.push(values.map(csvEscape).join(','));
  });

  return lines.join('\r\n');
}

function getScreenSeverity(screenItem) {
  const nodes = Number(screenItem.nodeCount || 0);
  const deps = Number(screenItem.dependenciesCount || 0);
  const links = Number(screenItem.dynamicLinkCount || 0);
  const broken = Number(screenItem.brokenDynamicLinkCount || 0);

  let score = 0;
  if (broken > 0) score += 3;
  if (nodes >= 3000) score += 3;
  else if (nodes >= 1600) score += 2;
  else if (nodes >= 700) score += 1;

  if (deps >= 80) score += 2;
  else if (deps >= 40) score += 1;

  if (links >= 600) score += 2;
  else if (links >= 250) score += 1;

  if (score >= 6) return { label: 'High', className: 'severity-high' };
  if (score >= 3) return { label: 'Medium', className: 'severity-medium' };
  return { label: 'Low', className: 'severity-low' };
}

function sortScreens(items, mode) {
  const rows = items.slice();

  switch (mode) {
    case 'name-asc':
      rows.sort((a, b) => String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'deps-desc':
      rows.sort((a, b) => Number(b.dependenciesCount || 0) - Number(a.dependenciesCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'links-desc':
      rows.sort((a, b) => Number(b.dynamicLinkCount || 0) - Number(a.dynamicLinkCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'broken-desc':
      rows.sort((a, b) => Number(b.brokenDynamicLinkCount || 0) - Number(a.brokenDynamicLinkCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
    case 'nodes-desc':
    default:
      rows.sort((a, b) => Number(b.nodeCount || 0) - Number(a.nodeCount || 0) || String(a.name || '').localeCompare(String(b.name || '')));
      break;
  }

  return rows;
}

function buildScreensCsv(rows) {
  const headers = ['name', 'baseType', 'fullPath', 'nodeCount', 'dependenciesCount', 'uiDependenciesCount', 'datatypeDependenciesCount', 'dynamicLinkCount', 'complexDynamicLinkCount', 'brokenDynamicLinkCount'];

  function csvEscape(value) {
    const text = String(value == null ? '' : value);
    if (/["",\n\r]/.test(text)) {
      return '""' + text.replace(/""/g, '""""') + '""';
    }
    return text;
  }

  const lines = [headers.join(',')];
  rows.forEach(item => {
    const values = [
      item.name,
      item.baseType,
      item.fullPath,
      item.nodeCount,
      item.dependenciesCount,
      item.uiDependenciesCount,
      item.datatypeDependenciesCount,
      item.dynamicLinkCount,
      item.complexDynamicLinkCount,
      item.brokenDynamicLinkCount
    ];
    lines.push(values.map(csvEscape).join(','));
  });

  return lines.join('\r\n');
}

function downloadScreensCsv() {
  const csv = buildScreensCsv(screenMapState.filteredScreens || []);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);

  const a = document.createElement('a');
  a.href = url;
  a.download = 'screens-export.csv';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function downloadDynamicLinksCsv() {
  const rows = dynamicLinksState.filteredTargets || [];
  const csvEscape = v => {
    const text = String(v == null ? '' : v);
    return /["",\n\r]/.test(text) ? '""' + text.replace(/""/g, '""""') + '""' : text;
  };
  const lines = ['targetPath,repetitions'];
  rows.forEach(item => lines.push(csvEscape(item.targetPath) + ',' + item.count));
  const blob = new Blob([lines.join('\r\n')], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);

  const a = document.createElement('a');
  a.href = url;
  a.download = 'dynamic-links-export.csv';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function downloadTypeMapCsv() {
  const csv = buildTypeMapCsv(typeMapState.filteredTypes || []);
  const blob = new Blob([csv], { type: 'text/csv;charset=utf-8;' });
  const url = URL.createObjectURL(blob);

  const a = document.createElement('a');
  a.href = url;
  a.download = 'type-map-export.csv';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

function setSectionOpen(sectionId, open) {
  const section = document.getElementById(sectionId);
  if (!section) return;
  section.classList.toggle('open', !!open);
}

function toggleSectionBlock(sectionId) {
  const section = document.getElementById(sectionId);
  if (!section) return false;
  section.classList.toggle('open');
  return false;
}

function toggleCategoryCard(categoryKey) {
  const card = document.getElementById('category-card-' + categoryKey);
  const body = document.getElementById('category-body-' + categoryKey);
  if (!card || !body) return false;

  const isOpen = card.classList.contains('open');
  card.classList.toggle('open', !isOpen);
  body.style.display = isOpen ? 'none' : '';

  if (!isOpen && categoryKey === 'commdrivers') { ensureCommDriversLoaded(); }
  if (!isOpen && categoryKey === 'opcua') { ensureOpcUaLoaded(); }
  if (!isOpen && categoryKey === 'netlogic') { ensureNetLogicLoaded(); }
  if (!isOpen && categoryKey === 'model') {
    ensureModelLoaded();
  }
  if (!isOpen && categoryKey === 'recipes') { ensureRecipesLoaded(); }
  if (!isOpen && categoryKey === 'datastores') { ensureDataStoresLoaded(); }
  if (!isOpen && categoryKey === 'loggers') { ensureLoggersLoaded(); }
  if (!isOpen && categoryKey === 'translations') { ensureTranslationsLoaded(); }
  if (!isOpen && categoryKey === 'security') { ensureSecurityLoaded(); }
  if (!isOpen && categoryKey === 'reports') { ensureReportsLoaded(); }
  if (!isOpen && categoryKey === 'retentivity') { ensureRetentivityLoaded(); }
  if (!isOpen && categoryKey === 'converters') { ensureConvertersLoaded(); }
  if (!isOpen && categoryKey === 'alarms') { ensureAlarmsLoaded(); }
  if (!isOpen && categoryKey === 'mqtt') { ensureMqttLoaded(); }
  return false;
}

function selectCategoryMacro(categoryKey, macroIndex) {
  const macroHost = document.getElementById('category-macros-' + categoryKey);
  const detailHost = document.getElementById('category-detail-' + categoryKey);
  if (!macroHost || !detailHost) return false;

  macroHost.querySelectorAll('.summary-card-action').forEach((btn, idx) => {
    btn.classList.toggle('active', idx === macroIndex);
  });

  detailHost.querySelectorAll('.category-macro-detail').forEach(item => {
    const idx = Number(item.getAttribute('data-macro-index') || -1);
    item.style.display = idx === macroIndex ? '' : 'none';
  });

  return false;
}

function scrollToSection(sectionId) {
  setSectionOpen(sectionId, true);
  const target = document.getElementById(sectionId);
  if (!target) return false;
  target.scrollIntoView({ behavior: 'smooth', block: 'start' });
  return false;
}

function getSectionKeyFromTab(tabName) {
  if (tabName === 'screens') return 'screens';
  if (tabName === 'links') return 'dynamicLinks';
  if (tabName === 'types') return 'types';
  return '';
}

function buildOverviewTabHtml(payload, section) {
  const src = section || payload || {};
  const overview = src.overview || (payload && payload.overview) || {};
  const typesCount = Number((payload && payload.summary && payload.summary.typesCount) || overview.typesCount || ((payload && payload.types) ? payload.types.length : 0));
  const brokenLinks = Number(overview.totalBrokenDynamicLinks || 0);
  const complexRatio = Number(overview.complexRatio || 0);

  return ""<div class='summary'>""
    + summaryCard('UI Nodes', formatNumber(overview.uiTotalNodes))
    + summaryCard('UI Objects', formatNumber(overview.uiObjectNodes))
    + summaryCard('Custom Types', formatNumber(typesCount))
    + summaryCard('Screens', formatNumber(overview.screensCount))
    + summaryCard('UI Dynamic Links', formatNumber(overview.totalDynamicLinks))
    + summaryCard('Max Depth', formatNumber(overview.maxDepth))
    + ""</div>""
    + ""<h3>UI Quality Signals</h3><div class='detail-section'><div class='table-scroll'><table class='table-compact'><thead><tr><th>Metric</th><th>Value</th></tr></thead><tbody>""
    + ""<tr><td>Broken Dynamic Links</td><td>"" + formatNumber(brokenLinks) + ""</td></tr>""
    + ""<tr><td>Complex Dynamic Links Ratio</td><td>"" + complexRatio.toFixed(1) + ""%</td></tr>""
    + ""<tr><td>Average Nodes per Screen</td><td>"" + (Number(overview.screensCount || 0) > 0 ? (Number(overview.uiTotalNodes || 0) / Number(overview.screensCount || 0)).toFixed(1) : '0.0') + ""</td></tr>""
    + ""</tbody></table></div></div>""
    + (brokenLinks > 0
      ? ""<div class='alert-box err'><div class='alert-title'>✕ "" + formatNumber(brokenLinks) + "" Broken Links</div><div class='alert-text'>Some dynamic links reference non-existent targets. Runtime errors may occur.</div></div>""
      : """")
    + (complexRatio > 30
      ? ""<div class='alert-box warn'><div class='alert-title'>⚠ High Converter Ratio ("" + complexRatio.toFixed(1) + ""%)</div><div class='alert-text'>Many dynamic links contain converters. Consider reducing converter usage for CPU efficiency.</div></div>""
      : """");
}

function buildScreensTabHtml(payload, section) {
  const src = section || payload || {};
  const screens = src.screens || (payload && payload.screens) || [];

  return ""<h3>Screens List</h3>"" + renderScreens(screens);
}

function buildTypesTabHtml(payload, section) {
  const types = (section && section.types) || (payload && payload.types) || [];
  return ""<h3>Type Interdependencies</h3>"" + renderTypes(types);
}

function renderTabPaneContent(tabName, payload, section) {
  const pane = document.getElementById(tabName);
  if (!pane) return;

  if (tabName === 'screens') {
    pane.innerHTML = buildScreensTabHtml(payload, section);
    const screens = (section && section.screens) || (payload && payload.screens) || [];
    initScreensControls(screens);
    return;
  }

  if (tabName === 'links') {
    const links = (section && section.dynamicLinks) || (payload && payload.dynamicLinks) || null;
    const findings = (payload && payload.findings && payload.findings.ui) || [];
    pane.innerHTML = renderCategoryFindings(findings) + renderDynamicLinksPanel(links);
    initDynamicLinksControls(links);
    return;
  }

  if (tabName === 'types') {
    pane.innerHTML = buildTypesTabHtml(payload, section);
    const types = (section && section.types) || (payload && payload.types) || [];
    initTypeMapControls(types);
    return;
  }
}

function loadSectionByKey(sectionKey) {
  const payload = reportRuntimeState.payload;
  if (!payload || !sectionKey) return Promise.resolve(null);

  const sectionPath = payload.sections && payload.sections[sectionKey] ? payload.sections[sectionKey] : null;
  if (!sectionPath) {
    if (sectionKey === 'crossCutting' && payload.crossCutting) return Promise.resolve({ crossCutting: payload.crossCutting, categoryMetrics: payload.categoryMetrics || {} });
    if (payload.categoryDetails && payload.categoryDetails[sectionKey]) return Promise.resolve(payload.categoryDetails[sectionKey]);
    return Promise.resolve(null);
  }

  if (reportRuntimeState.sectionCache[sectionKey]) return Promise.resolve(reportRuntimeState.sectionCache[sectionKey]);
  if (reportRuntimeState.sectionLoading[sectionKey]) return reportRuntimeState.sectionLoading[sectionKey];

  const prefersScript = String((window.location && window.location.protocol) || '').toLowerCase() === 'file:';
  const fetchPromise = prefersScript
    ? Promise.resolve(null)
    : fetch(sectionPath).then(r => (r.ok ? r.json() : null)).catch(() => null);

  reportRuntimeState.sectionLoading[sectionKey] = fetchPromise
    .then(section => section || ensureSectionScriptLoaded(payload, sectionKey, sectionPath))
    .then(section => {
      if (section) reportRuntimeState.sectionCache[sectionKey] = section;
      return section;
    })
    .finally(() => {
      delete reportRuntimeState.sectionLoading[sectionKey];
    });

  return reportRuntimeState.sectionLoading[sectionKey];
}

function ensureTabLoaded(tabName) {
  const payload = reportRuntimeState.payload;
  const pane = document.getElementById(tabName);
  if (!payload || !pane) return;
  if (pane.getAttribute('data-loaded') === '1') return;

  const sectionKey = getSectionKeyFromTab(tabName);
  if (!sectionKey || !(payload.sections && payload.sections[sectionKey])) {
    renderTabPaneContent(tabName, payload, null);
    pane.setAttribute('data-loaded', '1');
    return;
  }

  pane.innerHTML = ""<div class='detail-empty'>Loading data...</div>"";
  loadSectionByKey(sectionKey)
    .then(section => {
      if (!section) {
        pane.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load section data.</div></div>"";
        return;
      }
      renderTabPaneContent(tabName, payload, section);
      pane.setAttribute('data-loaded', '1');
    })
    .catch(() => {
      pane.innerHTML = ""<div class='alert-box err'><div class='alert-title'>Section unavailable</div><div class='alert-text'>Unable to load section data.</div></div>"";
    });
}

function openTab(tabName, options) {
  const ensureUiOpen = !options || options.ensureUiOpen !== false;
  const uiCard = document.getElementById('category-card-ui');
  const uiBody = document.getElementById('category-body-ui');
  if (ensureUiOpen && uiCard && uiBody) {
    uiCard.classList.add('open');
    uiBody.style.display = '';
  }

  const tabs = document.querySelectorAll('.nav-tab');
  const panes = document.querySelectorAll('.tab-pane');

  tabs.forEach(tab => tab.classList.toggle('active', tab.getAttribute('data-tab') === tabName));
  panes.forEach(pane => pane.classList.toggle('active', pane.id === tabName));
  ensureTabLoaded(tabName);
  return false;
}

function openTypeMap(event, anchorId, sourceScreen, sourceDependency) {
  if (event) event.preventDefault();

  if (sourceScreen || sourceDependency) {
    const source = sourceScreen ? String(sourceScreen) : 'unknown';
    const dep = sourceDependency ? String(sourceDependency) : '';
    typeMapState.jumpFrom = dep ? (""From "" + source + "" -> "" + dep) : (""From "" + source);
  }

  openTab('types');
  renderTypeMapBreadcrumb();

  let tries = 0;
  function jumpWhenReady() {
    const target = document.getElementById(anchorId);
    if (target) {
      target.scrollIntoView({ behavior: 'smooth', block: 'start' });
      target.classList.add('type-target-flash');
      window.setTimeout(() => target.classList.remove('type-target-flash'), 1600);
      return;
    }
    if (tries++ < 20)
      window.setTimeout(jumpWhenReady, 50);
  }

  window.setTimeout(jumpWhenReady, 0);

  return false;
}

function renderTypeMapBreadcrumb() {
  const host = document.getElementById('type-map-breadcrumb');
  if (!host) return;

  if (!typeMapState.jumpFrom) {
    host.innerHTML = '';
    host.style.display = 'none';
    return;
  }

  host.style.display = '';
  host.innerHTML = ""<div class='type-breadcrumb-pill'><span class='type-breadcrumb-label'>Jump context:</span><span class='type-breadcrumb-value'>""
    + escapeHtml(typeMapState.jumpFrom)
    + ""</span><button type='button' class='type-breadcrumb-clear' onclick='clearTypeMapBreadcrumb()'>Clear</button></div>"";
}

function clearTypeMapBreadcrumb() {
  typeMapState.jumpFrom = '';
  renderTypeMapBreadcrumb();
}

function renderDependencyList(dependencyTypeCounts, screenName) {
  const rows = Array.isArray(dependencyTypeCounts) ? dependencyTypeCounts : [];
  if (rows.length === 0) {
    return ""<div class='detail-empty'>No custom dependencies found for this screen.</div>"";
  }

  const uiRows = rows.filter(dep => (dep.kind || 'ui') === 'ui');
  const dtRows = rows.filter(dep => dep.kind === 'datatype');
  const otherRows = rows.filter(dep => dep.kind && dep.kind !== 'ui' && dep.kind !== 'datatype');

  function renderBucket(title, bucketRows) {
    if (!bucketRows.length) return '';

    const top = 10;
    const rows = bucketRows.slice(0, top);
    const hint = bucketRows.length > top
      ? ""<div class='chart-subtitle'>Top "" + top + "" of "" + formatNumber(bucketRows.length) + "" dependencies.</div>""
      : """";

    const html = rows.map(dep =>
      ""<li>""
      + ""<span class='detail-name'><a class='type-link' href='#"" + escapeHtml(dep.targetId || '') + ""' onclick='return openTypeMap(event, \"""" + escapeHtml(dep.targetId || '') + ""\"", \"""" + escapeHtml(screenName || '') + ""\"", \"""" + escapeHtml(dep.name || '') + ""\"")'>"" + escapeHtml(dep.name) + ""</a></span>""
      + ""<span class='dep-stat'>Instances: <strong>"" + formatNumber(dep.count) + ""</strong></span>""
      + ""<span class='dep-stat'>Nodes: <strong>"" + formatNumber(dep.nodeCount) + ""</strong></span>""
      + ""<span class='dep-stat'>Links: <strong>"" + formatNumber(dep.dynamicLinkCount) + ""</strong></span>""
      + ""</li>""
    ).join("""");

    return ""<h4>"" + escapeHtml(title) + ""</h4>""
      + hint
      + ""<ul class='detail-list dep-flat-list'>"" + html + ""</ul>"";
  }
  return ""<div class='dep-groups'>""
    + renderBucket('UI Objects', uiRows)
    + renderBucket('DataTypes', dtRows)
    + (otherRows.length ? renderBucket('Other', otherRows) : '')
    + ""</div>"";
}

function renderScreenCard(screen) {
    const dependencyTypeCounts = Array.isArray(screen.dependencyTypeCounts) ? screen.dependencyTypeCounts : [];
    const targets = Array.isArray(screen.topTargetPaths) ? screen.topTargetPaths : [];
    const depsHtml = renderDependencyList(dependencyTypeCounts, screen.name);
    const severity = getScreenSeverity(screen);

    const targetsHtml = targets.length > 0
      ? ""<ul class='detail-list detail-list-flat top-target-list'>"" + targets.slice(0, 10).map(t =>
          ""<li><span class='detail-name'>"" + escapeHtml(t.path) + ""</span><span class='detail-count'>Bindings: "" + formatNumber(t.bindings) + ""x</span></li>""
        ).join("""") + ""</ul>""
      : ""<div class='detail-empty'>No repeated dynamic link targets found.</div>"";

    const targetHint = targets.length > 10
      ? ""<div class='chart-subtitle'>Top 10 of "" + formatNumber(targets.length) + "" repeated target paths.</div>""
      : """";

    const brokenBadge = Number(screen.brokenDynamicLinkCount || 0) > 0
      ? ""<div class='metric-badge metric-badge-err'><span class='metric-label'>Broken links</span><span class='metric-value'>"" + formatNumber(screen.brokenDynamicLinkCount) + ""</span></div>""
      : """";

    const fullPath = String(screen.fullPath || '');
    const shortPath = abbreviatePathMiddle(fullPath, 110);

    return ""<div class='item-row "" + severity.className.replace('severity-', 'sev-') + ""'>""
      + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
      + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(screen.name) + ""</div><div class='item-meta' title='"" + escapeHtml(fullPath) + ""'>Base: "" + escapeHtml(screen.baseType) + "" • Path: "" + escapeHtml(shortPath) + ""</div></div>""
      + ""<div class='item-right'>""
      + ""<div class='metric-badge'><span class='metric-label'>Nodes</span><span class='metric-value'>"" + formatNumber(screen.nodeCount) + ""</span></div>""
      + ""<div class='metric-badge'><span class='metric-label'>Dependencies</span><span class='metric-value'>"" + formatNumber(screen.dependenciesCount) + ""</span></div>""
      + ""<div class='metric-badge'><span class='metric-label'>UI Objects</span><span class='metric-value'>"" + formatNumber(screen.uiDependenciesCount) + ""</span></div>""
      + ""<div class='metric-badge'><span class='metric-label'>DataTypes</span><span class='metric-value'>"" + formatNumber(screen.datatypeDependenciesCount) + ""</span></div>""
      + brokenBadge
      + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
      + ""</div></div>""
      + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
      + ""<div class='detail-section detail-section-wide'><h4>Custom Dependencies</h4>"" + depsHtml + ""</div>""
      + ""<div class='detail-section detail-section-wide'><h4>Dynamic Links</h4>""
      + ""<div class='detail-kv detail-kv-compact'>""
      + ""<div class='detail-kv-row'><span>Custom-typed nodes</span><strong>"" + formatNumber(screen.customTypedNodeCount) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Base-typed nodes</span><strong>"" + formatNumber(screen.baseTypedNodeCount) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Dependency-covered nodes</span><strong>"" + formatNumber(screen.dependencyCoveredNodeCount) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Total links</span><strong>"" + formatNumber(screen.dynamicLinkCount) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Complex links</span><strong>"" + formatNumber(screen.complexDynamicLinkCount) + ""</strong></div>""
      + ""<div class='detail-kv-row'><span>Broken links</span><strong>"" + formatNumber(screen.brokenDynamicLinkCount) + ""</strong></div>""
      + ""</div><h4>Top target paths</h4>"" + targetHint + targetsHtml + ""</div>""
      + ""</div></div></div>"";
}

function renderScreens(screens) {
  if (!Array.isArray(screens) || screens.length === 0) {
    return ""<div class='detail-empty'>No screens found.</div>"";
  }

  return ""<div class='screens-toolbar'>""
    + ""<input id='screen-filter-query' class='type-map-input' type='search' placeholder='Search by screen name, base type or path...' />""
    + ""<select id='screen-filter-kind' class='type-map-select'>""
    + ""<option value='all'>All screens</option>""
    + ""<option value='broken'>Only with broken links</option>""
    + ""<option value='heavy'>Only heavy (nodes >= 1000)</option>""
    + ""<option value='high-deps'>Only high deps (>= 30)</option>""
    + ""</select>""
    + ""<select id='screen-filter-sort' class='type-map-select'>""
    + ""<option value='nodes-desc'>Sort: Nodes (high-low)</option>""
    + ""<option value='deps-desc'>Sort: Dependencies (high-low)</option>""
    + ""<option value='links-desc'>Sort: Dynamic links (high-low)</option>""
    + ""<option value='broken-desc'>Sort: Broken links (high-low)</option>""
    + ""<option value='name-asc'>Sort: Name (A-Z)</option>""
    + ""</select>""
    + ""<button id='screens-export' class='chart-toggle' type='button'>Export CSV</button>""
    + ""</div>""
    + ""<div id='screens-stats' class='type-map-stats'></div>""
    + ""<div id='screens-results'></div>"";
}

function renderScreensResults() {
  const host = document.getElementById('screens-results');
  const stats = document.getElementById('screens-stats');
  if (!host || !stats) return;

  const rows = screenMapState.filteredScreens || [];
  if (!rows.length) {
    stats.innerHTML = ""<div class='detail-empty'>No screens match current filters.</div>"";
    host.innerHTML = """";
    return;
  }

  stats.innerHTML = ""<div class='type-map-stats-pill'>Showing <strong>"" + formatNumber(rows.length) + ""</strong> screen(s)</div>"";
  host.innerHTML = ""<div class='item-list item-list-scroll'>"" + rows.map(renderScreenCard).join('') + ""</div>"";
}

function updateScreenFilters() {
  const queryEl = document.getElementById('screen-filter-query');
  const kindEl = document.getElementById('screen-filter-kind');
  const sortEl = document.getElementById('screen-filter-sort');

  screenMapState.query = (queryEl ? String(queryEl.value || '') : '').trim().toLowerCase();
  screenMapState.filter = kindEl ? String(kindEl.value || 'all') : 'all';
  screenMapState.sort = sortEl ? String(sortEl.value || 'nodes-desc') : 'nodes-desc';

  let rows = screenMapState.allScreens.slice();

  if (screenMapState.filter === 'broken')
    rows = rows.filter(s => Number(s.brokenDynamicLinkCount || 0) > 0);
  else if (screenMapState.filter === 'heavy')
    rows = rows.filter(s => Number(s.nodeCount || 0) >= 1000);
  else if (screenMapState.filter === 'high-deps')
    rows = rows.filter(s => Number(s.dependenciesCount || 0) >= 30);

  if (screenMapState.query) {
    rows = rows.filter(s => {
      const name = String(s.name || '').toLowerCase();
      const base = String(s.baseType || '').toLowerCase();
      const path = String(s.fullPath || '').toLowerCase();
      return name.includes(screenMapState.query) || base.includes(screenMapState.query) || path.includes(screenMapState.query);
    });
  }

  screenMapState.filteredScreens = sortScreens(rows, screenMapState.sort);
  renderScreensResults();
}

function initScreensControls(screens) {
  screenMapState.allScreens = Array.isArray(screens) ? screens.slice() : [];
  screenMapState.filteredScreens = screenMapState.allScreens.slice();

  const queryEl = document.getElementById('screen-filter-query');
  const kindEl = document.getElementById('screen-filter-kind');
  const sortEl = document.getElementById('screen-filter-sort');
  const exportEl = document.getElementById('screens-export');

  if (queryEl) queryEl.addEventListener('input', updateScreenFilters);
  if (kindEl) kindEl.addEventListener('change', updateScreenFilters);
  if (sortEl) sortEl.addEventListener('change', updateScreenFilters);
  if (exportEl) exportEl.addEventListener('click', downloadScreensCsv);

  updateScreenFilters();
}

function renderTypeCard(t) {
  const usagePaths = Array.isArray(t.usagePaths) ? t.usagePaths : [];
  const internalDependencies = Array.isArray(t.internalDependencies) ? t.internalDependencies : [];
  const locationLabel = t.path ? escapeHtml(t.path) : escapeHtml(String(t.kind || 'other'));
  const typeLabel = t.inheritance ? escapeHtml(t.inheritance) : escapeHtml(String(t.kind || 'other'));
  const severity = getTypeSeverity(t);

  const usageTop = 20;
  const usageRows = usagePaths.slice(0, usageTop);
  const usageHint = usagePaths.length > usageTop
    ? ""<div class='chart-subtitle'>Top "" + usageTop + "" of "" + formatNumber(usagePaths.length) + "" paths.</div>""
    : """";
  const usagePathsHtml = usagePaths.length > 0
    ? usageHint + ""<ul class='detail-list usage-path-list'>"" + usageRows.map(path =>
        ""<li>""
        + ""<span class='mono usage-path' title='"" + escapeHtml(path) + ""'>"" + escapeHtml(abbreviatePathMiddle(path, 120)) + ""</span>""
        + ""</li>""
      ).join('') + ""</ul>""
    : ""<div class='detail-empty'>No usage paths recorded.</div>"";

  const internalDepsHtml = internalDependencies.length > 0
    ? ""<ul class='detail-list'>"" + internalDependencies.map(dep =>
        ""<li><a class='type-link' href='#"" + escapeHtml(dep.targetId || '') + ""' onclick='return openTypeMap(event, \"""" + escapeHtml(dep.targetId || '') + ""\"", \""Type Map\"", \"""" + escapeHtml(t.name || '') + ""\"")'>"" + escapeHtml(dep.name) + ""</a></li>""
      ).join('') + ""</ul>""
    : ""<div class='detail-empty'>No nested custom dependencies.</div>"";

  return ""<div class='item-row "" + severity.className.replace('severity-', 'sev-') + ""' id='"" + escapeHtml(t.anchorId || '') + ""'>""
    + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
    + ""<div class='item-left'><div class='item-name'>"" + escapeHtml(t.name) + ""</div><div class='item-meta' title='"" + locationLabel + ""'>"" + locationLabel + ""</div></div>""
    + ""<div class='item-right'>""
    + ""<div class='metric-badge'><span class='metric-label'>Usage paths</span><span class='metric-value'>"" + formatNumber(t.usageCount) + ""</span></div>""
    + ""<div class='metric-badge'><span class='metric-label'>Screens</span><span class='metric-value'>"" + formatNumber(t.screenCount) + ""</span></div>""
    + ""<div class='metric-badge'><span class='metric-label'>Content nodes</span><span class='metric-value'>"" + formatNumber(t.contentNodeCount) + ""</span></div>""
    + ""<div class='metric-badge'><span class='metric-label'>Internal deps</span><span class='metric-value'>"" + formatNumber(t.internalDependencyCount) + ""</span></div>""
    + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
    + ""</div></div>""
    + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
    + ""<div class='detail-section'><h4>Content</h4><div class='detail-kv detail-kv-compact'>""
    + ""<div class='detail-kv-row'><span>Type</span><strong>"" + typeLabel + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Location</span><strong>"" + locationLabel + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Content nodes</span><strong>"" + formatNumber(t.contentNodeCount) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Content variables</span><strong>"" + formatNumber(t.contentVariableCount) + ""</strong></div>""
    + ""<div class='detail-kv-row'><span>Dynamic links</span><strong>"" + formatNumber(t.contentDynamicLinkCount) + ""</strong></div>""
    + ""</div></div>""
    + ""<div class='detail-section detail-section-wide'><h4>Internal dependencies</h4>"" + internalDepsHtml + ""</div>""
    + ""<div class='detail-section detail-section-wide'><h4>Usage paths</h4>"" + usagePathsHtml + ""</div>""
    + ""</div></div></div>"";
}

function renderTypes(types) {
  if (!Array.isArray(types) || types.length === 0) {
    return ""<div class='detail-empty'>No type interdependency data.</div>"";
  }

  const kinds = Array.from(new Set(types.map(t => String(t.kind || 'other')))).sort((a, b) => a.localeCompare(b));

  return ""<div class='type-map-toolbar'>""
    + ""<input id='type-filter-query' class='type-map-input' type='search' placeholder='Search by type name or namespace...' />""
    + ""<select id='type-filter-kind' class='type-map-select'><option value='all'>All categories</option>""
    + kinds.map(kind => ""<option value='"" + escapeHtml(kind) + ""'>"" + escapeHtml(kind) + ""</option>"").join('')
    + ""</select>""
    + ""<select id='type-filter-sort' class='type-map-select'>""
    + ""<option value='name-asc'>Sort: Name (A-Z)</option>""
    + ""<option value='name-desc'>Sort: Name (Z-A)</option>""
    + ""<option value='usage-desc'>Sort: Usage (high-low)</option>""
    + ""<option value='content-desc'>Sort: Content nodes (high-low)</option>""
    + ""<option value='screens-desc'>Sort: Screens (high-low)</option>""
    + ""</select>""
    + ""<button id='type-map-export' class='chart-toggle' type='button'>Export CSV</button>""
    + ""</div>""
    + ""<div id='type-map-breadcrumb' class='type-map-breadcrumb' style='display:none'></div>""
    + ""<div id='type-map-stats' class='type-map-stats'></div>""
    + ""<div id='type-map-results'></div>"";
}

function renderTypeMapResults() {
  const host = document.getElementById('type-map-results');
  const stats = document.getElementById('type-map-stats');
  if (!host || !stats) return;

  const groups = [
    { key: 'ui', title: 'UI Objects' },
    { key: 'datatype', title: 'DataTypes' }
  ];
  const extraKinds = Array.from(new Set(typeMapState.filteredTypes.map(t => String(t.kind || 'other'))))
    .filter(kind => kind !== 'ui' && kind !== 'datatype')
    .sort((a, b) => a.localeCompare(b));
  extraKinds.forEach(kind => groups.push({ key: kind, title: kind.charAt(0).toUpperCase() + kind.slice(1) }));

  if (typeMapState.filteredTypes.length === 0) {
    stats.innerHTML = ""<div class='detail-empty'>No types match current filters.</div>"";
    host.innerHTML = """";
    return;
  }

  stats.innerHTML = ""<div class='type-map-stats-pill'>Showing <strong>"" + formatNumber(typeMapState.filteredTypes.length) + ""</strong> type(s)</div>"";

  host.innerHTML = ""<div class='item-list-scroll'>"" + groups.map(group => {
    const items = typeMapState.filteredTypes.filter(t => String(t.kind || 'other') === group.key);
    if (!items.length) return '';
    const header = group.key === 'ui' ? '' : ""<h3>"" + escapeHtml(group.title) + ""</h3>"";
    return header + ""<div class='item-list'>"" + items.map(renderTypeCard).join('') + ""</div>"";
  }).join('') + ""</div>"";
}

function updateTypeMapFilters() {
  const queryEl = document.getElementById('type-filter-query');
  const kindEl = document.getElementById('type-filter-kind');
  const sortEl = document.getElementById('type-filter-sort');

  typeMapState.query = (queryEl ? String(queryEl.value || '') : '').trim().toLowerCase();
  typeMapState.kind = kindEl ? String(kindEl.value || 'all') : 'all';
  typeMapState.sort = sortEl ? String(sortEl.value || 'name-asc') : 'name-asc';

  let rows = typeMapState.allTypes.slice();

  if (typeMapState.kind !== 'all')
    rows = rows.filter(t => String(t.kind || 'other') === typeMapState.kind);

  if (typeMapState.query) {
    rows = rows.filter(t => {
      const name = String(t.name || '').toLowerCase();
      const ns = String(t.namespaceUri || '').toLowerCase();
      return name.includes(typeMapState.query) || ns.includes(typeMapState.query);
    });
  }

  typeMapState.filteredTypes = sortTypes(rows, typeMapState.sort);
  renderTypeMapResults();
}

function initTypeMapControls(types) {
  typeMapState.allTypes = Array.isArray(types) ? types.slice() : [];
  typeMapState.filteredTypes = typeMapState.allTypes.slice();

  const queryEl = document.getElementById('type-filter-query');
  const kindEl = document.getElementById('type-filter-kind');
  const sortEl = document.getElementById('type-filter-sort');
  const exportEl = document.getElementById('type-map-export');

  if (queryEl) queryEl.addEventListener('input', updateTypeMapFilters);
  if (kindEl) kindEl.addEventListener('change', updateTypeMapFilters);
  if (sortEl) sortEl.addEventListener('change', updateTypeMapFilters);
  if (exportEl) exportEl.addEventListener('click', downloadTypeMapCsv);

  updateTypeMapFilters();
  renderTypeMapBreadcrumb();
}

function renderDynamicLinksPanel(dynamicLinks) {
  return ""<h3>Repeated Targets</h3>""
    + ""<div class='dynamic-links-toolbar'>""
    + ""<input id='dynlink-filter-query' class='type-map-input' type='search' placeholder='Search by target path...' />""
    + ""<button id='dynlink-export' class='chart-toggle' type='button'>Export CSV</button>""
    + ""</div>""
    + ""<div id='dynlink-stats' class='type-map-stats'></div>""
    + ""<div id='dynlink-results'></div>"";
}

function renderDynamicLinksResults() {
  const stats = document.getElementById('dynlink-stats');
  const host = document.getElementById('dynlink-results');
  if (!stats || !host) return;

  const rows = dynamicLinksState.filteredTargets || [];
  stats.innerHTML = ""<div class='type-map-stats-pill'>Showing <strong>"" + formatNumber(rows.length) + ""</strong> target(s)</div>"";

  const sourceCap = 100;
  host.innerHTML = rows.length
    ? ""<div class='item-list item-list-scroll'>"" + rows.map(item => {
        const sources = Array.isArray(item.sources) ? item.sources : [];
        const shown = sources.slice(0, sourceCap);
        const hint = sources.length > sourceCap
          ? ""<div class='chart-subtitle'>Showing "" + sourceCap + "" of "" + formatNumber(sources.length) + "" usages.</div>""
          : """";
        const list = shown.length
          ? ""<ul class='detail-list'>"" + shown.map(s => ""<li class='mono' title='"" + escapeHtml(s) + ""'>"" + escapeHtml(abbreviatePathMiddle(s, 130)) + ""</li>"").join('') + ""</ul>""
          : ""<div class='detail-empty'>No source paths recorded.</div>"";
        return ""<div class='item-row'>""
          + ""<div class='item-head' onclick='toggleExpand(this.closest(\"".item-row\""))'>""
          + ""<div class='item-left'><div class='item-name mono' title='"" + escapeHtml(item.targetPath) + ""'>"" + escapeHtml(abbreviatePathMiddle(item.targetPath, 120)) + ""</div><div class='item-meta'>Used by "" + formatNumber(item.count) + "" dynamic link"" + (item.count === 1 ? """" : ""s"") + ""</div></div>""
          + ""<div class='item-right'>""
          + ""<div class='metric-badge'><span class='metric-label'>Repetitions</span><span class='metric-value'>"" + formatNumber(item.count) + ""</span></div>""
          + ""<button class='expand-btn' type='button' aria-label='Expand details'></button>""
          + ""</div></div>""
          + ""<div class='item-details'><div class='detail-content detail-content-screens'>""
          + ""<div class='detail-section detail-section-wide'><h4>Used in</h4>"" + hint + list + ""</div>""
          + ""</div></div>""
          + ""</div>"";
      }).join('') + ""</div>""
    : ""<div class='detail-empty'>No dynamic links found.</div>"";
}

function updateDynamicLinksFilters() {
  const queryEl = document.getElementById('dynlink-filter-query');
  dynamicLinksState.query = (queryEl ? String(queryEl.value || '') : '').trim().toLowerCase();

  let rows = dynamicLinksState.targets.slice();
  if (dynamicLinksState.query) {
    rows = rows.filter(t => t.targetPath.toLowerCase().includes(dynamicLinksState.query));
  }

  dynamicLinksState.filteredTargets = rows;
  renderDynamicLinksResults();
}

function initDynamicLinksControls(dynamicLinks) {
  const data = dynamicLinks || { items: [] };

  const map = {};
  (Array.isArray(data.items) ? data.items : []).forEach(item => {
    const target = String((item && item.targetPath) || '').trim();
    if (!target) return;
    if (!map[target]) map[target] = { targetPath: target, count: 0, sources: [] };
    map[target].count++;
    const src = String((item && item.sourcePath) || '').trim();
    if (src) map[target].sources.push(src);
  });

  const targets = Object.keys(map).map(t => map[t]);
  targets.sort((a, b) => b.count - a.count || a.targetPath.localeCompare(b.targetPath));

  dynamicLinksState.targets = targets;
  dynamicLinksState.filteredTargets = targets.slice();
  dynamicLinksState.query = '';

  const queryEl = document.getElementById('dynlink-filter-query');
  const exportEl = document.getElementById('dynlink-export');
  if (queryEl) queryEl.addEventListener('input', updateDynamicLinksFilters);
  if (exportEl) exportEl.addEventListener('click', downloadDynamicLinksCsv);

  updateDynamicLinksFilters();
}

function renderApp(data) {
  const app = document.getElementById('app') || (() => {
    const root = document.createElement('div');
    root.id = 'app';
    document.body.innerHTML = '';
    document.body.appendChild(root);
    return root;
  })();

  app.innerHTML = ""<header><div class='header-content'><div class='header-top'><div class='header-left'><div class='header-brand'>""
    + ""<img class='header-logo header-logo-light' src='ProjectDiagnostics.assets/logo_FTOptix_white.svg' alt='FT Optix logo' />""
    + ""<img class='header-logo header-logo-dark' src='ProjectDiagnostics.assets/logo_FTOptix_white.svg' alt='FT Optix logo' />""
    + ""</div><div class='header-text'><h1>Project Analysis - "" + escapeHtml(data.projectName || 'Project') + "" - Generated "" + escapeHtml(data.generatedAt || '') + ""</h1><p>""
    + escapeHtml(data.projectPath || '')
    + ""</p></div></div><div class='header-right'></div></div></div></header>""
    + ""<div class='container'><div class='content'>""
    + renderGlobalOverviewSection(data)
    + renderProjectCategoriesPanel(data)
    + ""</div></div><footer><p>FT Optix Project Analyzer • <a href='optix_project_analyzer_help_memo.html' style='color: var(--accent); text-decoration: none;'>Help &amp; Memo</a></p></footer>"";

  reportRuntimeState.payload = data;
  reportRuntimeState.sectionCache = {};
  reportRuntimeState.sectionLoading = {};
  reportRuntimeState.sectionScriptLoading = {};
  initGlobalOverviewSection(data);
  openTab('screens', { ensureUiOpen: false });
}

function loadData() {
  if (window.reportDataManifest) {
    return Promise.resolve(window.reportDataManifest);
  }
  if (window.reportData) {
    return Promise.resolve(window.reportData);
  }

  return fetch('ProjectDiagnostics.assets/report-manifest.json')
    .then(r => (r.ok ? r.json() : null))
    .catch(() => null)
    .then(manifest => {
      if (manifest) return manifest;
      return fetch('ProjectDiagnostics.assets/report-data.json')
        .then(r => (r.ok ? r.json() : null))
        .catch(() => null);
    });
}

loadData().then(payload => {
  if (!payload) {
    const app = document.getElementById('app');
    if (app) {
      app.innerHTML = ""<div class='container'><div class='content'><div class='alert-box err'><div class='alert-title'>No data available</div><div class='alert-text'>Run diagnostics to generate ProjectDiagnostics.assets/report-data.json.</div></div></div></div>"";
    }
    return;
  }
  renderApp(payload);
});

window.toggleExpand = toggleExpand;
window.openTypeMap = openTypeMap;
window.clearTypeMapBreadcrumb = clearTypeMapBreadcrumb;
window.toggleSectionBlock = toggleSectionBlock;
window.toggleCategoryCard = toggleCategoryCard;
window.selectCategoryMacro = selectCategoryMacro;
window.scrollToSection = scrollToSection;
window.downloadCategoryMetricsCsv = downloadCategoryMetricsCsv;
window.downloadCrossCuttingCsv = downloadCrossCuttingCsv;
window.ensureCategoryLazyLoaded = ensureCategoryLazyLoaded;
";

}
// <SHERLOCK_ASSETS_END>
