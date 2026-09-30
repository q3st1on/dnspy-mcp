using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.MCP.Core.Helpers;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Tools {
    /// <summary>
    /// Extension-only tool class providing the two UI-backed MCP tools
    /// (<c>get_selected_node</c>, <c>refresh_ui</c>) plus internal helpers used by
    /// <see cref="dnSpy.MCP.Adapters.DnSpyTreeRefreshNotifier"/> for namespace rename
    /// restructuring.
    ///
    /// Unlike the Core tool classes (which receive <see cref="dnSpy.MCP.Core.Mcp.McpContext"/>
    /// via constructor injection), this class stays static because it is the only Extension-only
    /// tool set and it needs direct WPF dispatcher access. <see cref="Initialize"/> is called
    /// once by <c>TheExtension.OnEvent(AppLoaded)</c> to populate the tree view and tab service
    /// resolved from the MEF <c>IServiceLocator</c>.
    ///
    /// <c>get_selected_node</c> follows the same identity contract as the Core tools: the
    /// selected node is reported as a token-keyed identity (token + name) so the caller can
    /// immediately address it in a rename/patch/xref call.
    /// </summary>
    public static class TreeViewTools {
        const string ToolGetSelectedNode = "get_selected_node";
        const string ToolRefreshUi = "refresh_ui";

        static IDocumentTreeView? _treeView;
        static IDocumentTabService? _tabService;

        /// <summary>
        /// Populate the static tree view / tab service references. Called once during
        /// <c>TheExtension.OnEvent(AppLoaded)</c>. Safe to call with nulls (tools will report
        /// "not available" at call time).
        /// </summary>
        internal static void Initialize(IDocumentTreeView? treeView, IDocumentTabService? tabService) {
            _treeView = treeView;
            _tabService = tabService;
        }

        static T? RunOnUIThread<T>(Func<T> action) where T : class? {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return null;

            if (dispatcher.CheckAccess())
                return action();

            return dispatcher.Invoke(action, DispatcherPriority.Normal);
        }

        internal static void RunOnUIThread(Action action) {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action, DispatcherPriority.Normal);
        }

        [Description("Gets the currently selected node in the dnSpy tree view as a token-keyed identity payload: metadata token (with moduleMvid) plus the node's name and kind. Feed that token straight into rename_*, decompile_*, get_* calls. Namespace nodes carry their anchor type token (namespaces have no metadata row of their own).")]
        public static string GetSelectedNode() {
            var treeView = _treeView;
            if (treeView == null)
                return ToolResponse.Failure(ToolGetSelectedNode, "TreeView not available.");

            try {
                var result = RunOnUIThread(() => DescribeSelection(treeView));
                return result ?? ToolResponse.Failure(ToolGetSelectedNode, "No UI thread available (dnSpy is not running).");
            }
            catch (Exception ex) {
                return ToolResponse.Failure(ToolGetSelectedNode, $"Error accessing tree view: {ex.Message}");
            }
        }

        /// <summary>Builds the identity payload for the current selection (UI thread only).</summary>
        static string DescribeSelection(IDocumentTreeView treeView) {
            if (treeView.TreeView?.SelectedItem is not DocumentTreeNodeData selected)
                return ToolResponse.Failure(ToolGetSelectedNode, "No node is selected in the dnSpy tree view.");

            // NodePathName is a struct, so no null-conditional on the value itself.
            var nodePathName = selected.NodePathName.Name ?? "";
            var identity = IdentityForNode(selected, nodePathName);

            return ToolResponse.Success(ToolGetSelectedNode, identity, new JsonObject {
                ["nodeType"] = selected.GetType().Name,
                ["nodePathName"] = nodePathName,
            });
        }

        /// <summary>
        /// Maps a dnSpy tree node to a metadata-token identity. Every node kind that
        /// wraps a metadata entity reports its token; the remaining kinds report a null
        /// token with an explicit reason instead of pretending to have one.
        /// </summary>
        static JsonObject IdentityForNode(DocumentTreeNodeData selected, string nodePathName) {
            switch (selected) {
                case TypeNode typeNode when typeNode.TypeDef is not null:
                    return MetadataIdentity.ForType(typeNode.TypeDef);
                case MethodNode methodNode when methodNode.MethodDef is not null:
                    return MetadataIdentity.ForMethod(methodNode.MethodDef);
                case FieldNode fieldNode when fieldNode.FieldDef is not null:
                    return MetadataIdentity.ForField(fieldNode.FieldDef);
                case PropertyNode propertyNode when propertyNode.PropertyDef is not null:
                    return MetadataIdentity.ForProperty(propertyNode.PropertyDef);
                case EventNode eventNode when eventNode.EventDef is not null:
                    return MetadataIdentity.ForEvent(eventNode.EventDef);
                case AssemblyDocumentNode assemblyNode when assemblyNode.Document?.ModuleDef is ModuleDef assemblyModule:
                    return MetadataIdentity.ForModule(assemblyModule);
                case NamespaceNode namespaceNode: {
                    var module = FindOwningModule(namespaceNode);
                    if (module is not null)
                        return MetadataIdentity.ForNamespace(module, namespaceNode.Name ?? "");
                    return Untokenized(MetadataIdentity.NamespaceKind, namespaceNode.Name ?? nodePathName,
                        "namespace node without a resolvable owning module");
                }
                default: {
                    // Fallback: some node kinds carry a token-bearing entity in TreeNode.Data.
                    if (selected.TreeNode?.Data is IMDTokenProvider tokenProvider && tokenProvider.MDToken.Raw != 0) {
                        var module = (tokenProvider as IMemberRef)?.Module;
                        return MetadataIdentity.For(tokenProvider, tokenProvider.GetType().Name, module,
                            nodePathName, null);
                    }
                    return Untokenized(selected.GetType().Name, nodePathName,
                        "this node kind has no metadata token; use assembly_list_namespaces / search_types / search_methods to obtain one");
                }
            }
        }

        static JsonObject Untokenized(string kind, string? name, string reason) {
            var o = MetadataIdentity.For(null, kind, null, name ?? "");
            o["reason"] = reason;
            return o;
        }

        /// <summary>Walks up from a node to the module node that owns it.</summary>
        static ModuleDef? FindOwningModule(DocumentTreeNodeData node) {
            var treeNode = node.TreeNode;
            while (treeNode is not null) {
                if (treeNode.Data is DsDocumentNode documentNode && documentNode.Document?.ModuleDef is ModuleDef module)
                    return module;
                treeNode = treeNode.Parent;
            }
            return null;
        }

        /// <summary>
        /// Dispatcher-aware tree refresh for use by rename/patch tools.
        /// </summary>
        internal static void RefreshTreeViewOnUIThread() {
            var treeView = _treeView;
            if (treeView == null) {
                McpLogger.Warn("RefreshTreeView: IDocumentTreeView not resolved");
                return;
            }

            try {
                RunOnUIThread(() => {
                    var tv = treeView.TreeView;
                    if (tv == null) return;

                    tv.RefreshAllNodes();
                });
            }
            catch (Exception ex) {
                McpLogger.Error(ex, "RefreshTreeView failed");
            }
        }

        internal static void UpdateNamespaceNode(string assembly, string oldNamespace, string newNamespace) {
            var treeView = _treeView;
            if (treeView == null) {
                McpLogger.Warn("UpdateNamespaceNode: IDocumentTreeView not resolved");
                return;
            }

            try {
                RunOnUIThread(() => {
                    var tv = treeView.TreeView;
                    if (tv == null) return;

                    foreach (var asmTreeNode in tv.Root.Children) {
                        if (asmTreeNode.Data is not AssemblyDocumentNode asmNode) continue;
                        if (!string.Equals(asmNode.Document.ModuleDef?.Assembly?.Name?.String, assembly, StringComparison.OrdinalIgnoreCase))
                            continue;

                        asmTreeNode.EnsureChildrenLoaded();
                        var modNode = asmTreeNode.DataChildren.OfType<ModuleDocumentNode>().FirstOrDefault();
                        if (modNode == null) continue;

                        modNode.TreeNode.EnsureChildrenLoaded();
                        var oldNsNode = modNode.FindNode(oldNamespace);
                        if (oldNsNode == null) continue;

                        oldNsNode.TreeNode.EnsureChildrenLoaded();
                        var existingNewNs = modNode.FindNode(newNamespace);

                        if (existingNewNs != null) {
                            var typeTreeNodes = oldNsNode.TreeNode.Children.ToList();
                            oldNsNode.TreeNode.Children.Clear();
                            foreach (var typeTreeNode in typeTreeNodes)
                                existingNewNs.TreeNode.AddChild(typeTreeNode);
                            oldNsNode.TreeNode.Parent?.Children.Remove(oldNsNode.TreeNode);
                            existingNewNs.TreeNode.RefreshUI();
                        }
                        else {
                            oldNsNode.Name = newNamespace;
                            oldNsNode.TreeNode.RefreshUI();
                        }

                        _tabService?.RefreshModifiedDocument(modNode.Document);
                    }
                });
            }
            catch (Exception ex) {
                McpLogger.Error(ex, "UpdateNamespaceNode failed");
            }
        }

        [Description("Refreshes all open document tabs in dnSpy to reflect assembly modifications made by MCP tools. Returns a token-model acknowledgement (no element target — this is a UI action, not a metadata lookup).")]
        public static string RefreshUI() {
            var treeView = _treeView;
            if (treeView == null)
                return ToolResponse.Failure(ToolRefreshUi, "TreeView not available.");

            try {
                RunOnUIThread(() => {
                    treeView.TreeView?.RefreshAllNodes();
                });
            }
            catch (Exception ex) {
                return ToolResponse.Failure(ToolRefreshUi, $"Error refreshing UI: {ex.Message}");
            }

            return ToolResponse.Success(ToolRefreshUi, new JsonObject {
                ["refreshed"] = true,
                ["target"] = null,
                ["message"] = "UI refreshed. Tool payloads keep addressing elements by metadata token; names in the tree may have changed.",
            });
        }
    }
}
