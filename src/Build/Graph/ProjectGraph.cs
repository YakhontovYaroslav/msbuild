﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Shared;

namespace Microsoft.Build.Experimental.Graph
{
    /// <summary>
    ///     Represents a graph of evaluated projects.
    /// </summary>
    [DebuggerDisplay(@"#roots={GraphRoots.Count}, #nodes={ProjectNodes.Count}, #entryPoints={EntryPointNodes.Count}")]
    public sealed class ProjectGraph
    {
        /// <summary>
        ///     A callback used for constructing a <see cref="ProjectInstance" /> for a specific
        ///     <see cref="ProjectGraphEntryPoint" /> instance.
        /// </summary>
        /// <param name="projectPath">The path to the project file to parse.</param>
        /// <param name="globalProperties">The global properties to be used for creating the ProjectInstance.</param>
        /// <param name="projectCollection">The <see cref="ProjectCollection" /> context for parsing.</param>
        /// <returns>A <see cref="ProjectInstance" /> instance. This value must not be null.</returns>
        /// <remarks>
        ///     The default version of this delegate used by ProjectGraph simply calls the
        ///     ProjectInstance constructor with information from the parameters. This delegate
        ///     is provided as a hook to allow scenarios like creating a <see cref="Project" />
        ///     instance before converting it to a ProjectInstance for use by the ProjectGraph.
        ///     The returned ProjectInstance will be stored and provided with the ProjectGraph.
        ///     If this callback chooses to generate an immutable ProjectInstance, e.g. by
        ///     using <see cref="Project.CreateProjectInstance()" /> with the flag
        ///     <see cref="ProjectInstanceSettings.Immutable" />, the resulting ProjectGraph
        ///     nodes might not be buildable.
        ///     To avoid corruption of the graph and subsequent builds based on the graph:
        ///     - all callback parameters must be utilized for creating the ProjectInstance, without any mutations
        ///     - the project instance should not be mutated in any way, its state should be a
        ///     full fidelity representation of the project file
        /// </remarks>
        public delegate ProjectInstance ProjectInstanceFactoryFunc(
            string projectPath,
            Dictionary<string, string> globalProperties,
            ProjectCollection projectCollection);

        private readonly Lazy<IReadOnlyCollection<ProjectGraphNode>> _projectNodesTopologicallySorted;

        private GraphBuilder.GraphEdges Edges { get; }

        internal GraphBuilder.GraphEdges TestOnly_Edges => Edges;

        /// <summary>
        ///     Gets the project nodes representing the entry points.
        /// </summary>
        public IReadOnlyCollection<ProjectGraphNode> EntryPointNodes { get; }

        /// <summary>
        ///     Get an unordered collection of all project nodes in the graph.
        /// </summary>
        public IReadOnlyCollection<ProjectGraphNode> ProjectNodes { get; }

        /// <summary>
        ///     Get a topologically sorted collection of all project nodes in the graph.
        ///     Referenced projects appear before the referencing projects.
        /// </summary>
        public IReadOnlyCollection<ProjectGraphNode> ProjectNodesTopologicallySorted => _projectNodesTopologicallySorted.Value;

        public IReadOnlyCollection<ProjectGraphNode> GraphRoots { get; }

        /// <summary>
        ///     Constructs a graph starting from the given project file, evaluating with the global project collection and no
        ///     global properties.
        /// </summary>
        /// <param name="entryProjectFile">The project file to use as the entry point in constructing the graph</param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(string entryProjectFile)
            : this(new ProjectGraphEntryPoint(entryProjectFile).AsEnumerable(), ProjectCollection.GlobalProjectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project files, evaluating with the global project collection and no
        ///     global properties.
        /// </summary>
        /// <param name="entryProjectFiles">The project files to use as the entry points in constructing the graph</param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(IEnumerable<string> entryProjectFiles)
            : this(ProjectGraphEntryPoint.CreateEnumerable(entryProjectFiles), ProjectCollection.GlobalProjectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project file, evaluating with the provided project collection and no
        ///     global properties.
        /// </summary>
        /// <param name="entryProjectFile">The project file to use as the entry point in constructing the graph</param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(string entryProjectFile, ProjectCollection projectCollection)
            : this(new ProjectGraphEntryPoint(entryProjectFile).AsEnumerable(), projectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project files, evaluating with the provided project collection and no
        ///     global properties.
        /// </summary>
        /// <param name="entryProjectFiles">The project files to use as the entry points in constructing the graph</param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(IEnumerable<string> entryProjectFiles, ProjectCollection projectCollection)
            : this(ProjectGraphEntryPoint.CreateEnumerable(entryProjectFiles), projectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project file, evaluating with the global project collection and no
        ///     global properties.
        /// </summary>
        /// <param name="entryProjectFile">The project file to use as the entry point in constructing the graph</param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <param name="projectInstanceFactory">
        ///     A delegate used for constructing a <see cref="ProjectInstance" />, called for each
        ///     project created during graph creation. This value can be null, which uses
        ///     a default implementation that calls the ProjectInstance constructor. See the remarks
        ///     on the <see cref="ProjectInstanceFactoryFunc" /> for other scenarios.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        ///     If a null reference is returned from <paramref name="projectInstanceFactory" />, the InnerException contains
        ///     <see cref="InvalidOperationException" />
        /// </exception>
        /// </exception>
        public ProjectGraph(string entryProjectFile, ProjectCollection projectCollection, ProjectInstanceFactoryFunc projectInstanceFactory)
            : this(new ProjectGraphEntryPoint(entryProjectFile).AsEnumerable(), projectCollection, projectInstanceFactory)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project file, evaluating with the provided global properties and the
        ///     global project collection.
        /// </summary>
        /// <param name="entryProjectFile">The project file to use as the entry point in constructing the graph</param>
        /// <param name="globalProperties">
        ///     The global properties to use for all projects. May be null, in which case no global
        ///     properties will be set.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(string entryProjectFile, IDictionary<string, string> globalProperties)
            : this(new ProjectGraphEntryPoint(entryProjectFile, globalProperties).AsEnumerable(), ProjectCollection.GlobalProjectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project files, evaluating with the provided global properties and the
        ///     global project collection.
        /// </summary>
        /// <param name="entryProjectFiles">The project files to use as the entry points in constructing the graph</param>
        /// <param name="globalProperties">
        ///     The global properties to use for all projects. May be null, in which case no global
        ///     properties will be set.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(IEnumerable<string> entryProjectFiles, IDictionary<string, string> globalProperties)
            : this(ProjectGraphEntryPoint.CreateEnumerable(entryProjectFiles, globalProperties), ProjectCollection.GlobalProjectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project file, evaluating with the provided global properties and the
        ///     provided project collection.
        /// </summary>
        /// <param name="entryProjectFile">The project file to use as the entry point in constructing the graph</param>
        /// <param name="globalProperties">
        ///     The global properties to use for all projects. May be null, in which case no global
        ///     properties will be set.
        /// </param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(string entryProjectFile, IDictionary<string, string> globalProperties, ProjectCollection projectCollection)
            : this(new ProjectGraphEntryPoint(entryProjectFile, globalProperties).AsEnumerable(), projectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given project files, evaluating with the provided global properties and the
        ///     provided project collection.
        /// </summary>
        /// <param name="entryProjectFiles">The project files to use as the entry points in constructing the graph</param>
        /// <param name="globalProperties">
        ///     The global properties to use for all projects. May be null, in which case no global
        ///     properties will be set.
        /// </param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(IEnumerable<string> entryProjectFiles, IDictionary<string, string> globalProperties, ProjectCollection projectCollection)
            : this(ProjectGraphEntryPoint.CreateEnumerable(entryProjectFiles, globalProperties), projectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given graph entry point, evaluating with the global project collection.
        /// </summary>
        /// <param name="entryPoint">The entry point to use in constructing the graph</param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(ProjectGraphEntryPoint entryPoint)
            : this(entryPoint.AsEnumerable(), ProjectCollection.GlobalProjectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given graph entry points, evaluating with the global project collection.
        /// </summary>
        /// <param name="entryPoints">The entry points to use in constructing the graph</param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(IEnumerable<ProjectGraphEntryPoint> entryPoints)
            : this(entryPoints, ProjectCollection.GlobalProjectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given graph entry point, evaluating with the provided project collection.
        /// </summary>
        /// <param name="entryPoint">The entry point to use in constructing the graph</param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        /// </exception>
        public ProjectGraph(ProjectGraphEntryPoint entryPoint, ProjectCollection projectCollection)
            : this(entryPoint.AsEnumerable(), projectCollection, null)
        {
        }

        /// <summary>
        ///     Constructs a graph starting from the given graph entry points, evaluating with the provided project collection.
        /// </summary>
        /// <param name="entryPoints">The entry points to use in constructing the graph</param>
        /// <param name="projectCollection">
        ///     The collection with which all projects in the graph should be associated. May not be
        ///     null.
        /// </param>
        /// <param name="projectInstanceFactory">
        ///     A delegate used for constructing a <see cref="ProjectInstance" />, called for each
        ///     project created during graph creation. This value can be null, which uses
        ///     a default implementation that calls the ProjectInstance constructor. See the remarks
        ///     on <see cref="ProjectInstanceFactoryFunc" /> for other scenarios.
        /// </param>
        /// <exception cref="AggregateException">
        ///     If the evaluation of any project in the graph fails, the InnerException contains
        ///     <see cref="InvalidProjectFileException" />
        ///     If a null reference is returned from <paramref name="projectInstanceFactory" />, the InnerException contains
        ///     <see cref="InvalidOperationException" />
        /// </exception>
        /// <exception cref="CircularDependencyException">
        ///     If the evaluation is successful but the project graph contains a circular
        ///     dependency
        /// </exception>
        public ProjectGraph(
            IEnumerable<ProjectGraphEntryPoint> entryPoints,
            ProjectCollection projectCollection,
            ProjectInstanceFactoryFunc projectInstanceFactory)
        {
            ErrorUtilities.VerifyThrowArgumentNull(projectCollection, nameof(projectCollection));

            projectInstanceFactory = projectInstanceFactory ?? DefaultProjectInstanceFactory;

            var graphBuilder = new GraphBuilder(ProjectInterpretation.Instance);

            graphBuilder.BuildGraph(entryPoints, projectCollection, projectInstanceFactory);

            EntryPointNodes = graphBuilder.EntryPointNodes;
            GraphRoots = graphBuilder.RootNodes;
            ProjectNodes = graphBuilder.ProjectNodes;
            Edges = graphBuilder.Edges;

            _projectNodesTopologicallySorted = new Lazy<IReadOnlyCollection<ProjectGraphNode>>(() => TopologicalSort(GraphRoots, ProjectNodes));
        }

        internal string ToDot()
        {
            var nodeCount = 0;
            return ToDot(node => nodeCount++.ToString());
        }

        internal string ToDot(Func<ProjectGraphNode, string> nodeIdProvider)
        {
            ErrorUtilities.VerifyThrowArgumentNull(nodeIdProvider, nameof(nodeIdProvider));

            var nodeIds = new ConcurrentDictionary<ProjectGraphNode, string>();

            var sb = new StringBuilder();

            sb.Append("digraph g\n{\n\tnode [shape=box]\n");

            foreach (var node in ProjectNodes)
            {
                var nodeId = nodeIds.GetOrAdd(node, n => nodeIdProvider(n));

                var nodeName = Path.GetFileNameWithoutExtension(node.ProjectInstance.FullPath);
                var globalPropertiesString = string.Join(
                    "<br/>",
                    node.ProjectInstance.GlobalProperties.OrderBy(kvp => kvp.Key)
                        .Select(kvp => $"{kvp.Key}={kvp.Value}"));

                sb.AppendLine($"\t{nodeId} [label=<{nodeName}<br/>{globalPropertiesString}>]");

                foreach (var reference in node.ProjectReferences)
                {
                    var referenceId = nodeIds.GetOrAdd(reference, n => nodeIdProvider(n));

                    sb.AppendLine($"\t{nodeId} -> {referenceId}");
                }
            }

            sb.Append("}");

            return sb.ToString();
        }

        private static IReadOnlyCollection<ProjectGraphNode> TopologicalSort(
            IReadOnlyCollection<ProjectGraphNode> graphRoots,
            IReadOnlyCollection<ProjectGraphNode> graphNodes)
        {
            var toposort = new List<ProjectGraphNode>(graphNodes.Count);
            var partialRoots = new Queue<ProjectGraphNode>(graphNodes.Count);
            var inDegree = graphNodes.ToDictionary(n => n, n => n.ReferencingProjects.Count);

            foreach (var root in graphRoots)
            {
                partialRoots.Enqueue(root);
            }

            while (partialRoots.Count != 0)
            {
                var partialRoot = partialRoots.Dequeue();

                toposort.Add(partialRoot);

                foreach (var reference in partialRoot.ProjectReferences)
                {
                    if (--inDegree[reference] == 0)
                    {
                        partialRoots.Enqueue(reference);
                    }
                }
            }

            ErrorUtilities.VerifyThrow(toposort.Count == graphNodes.Count, "sorted node count must be equal to total node count");

            toposort.Reverse();

            return toposort;
        }

        /// <summary>
        ///     Gets the target list to be executed for every project in the graph, given a particular target list for the entry
        ///     project.
        /// </summary>
        /// <remarks>
        ///     This method uses the ProjectReferenceTargets items to determine the targets to run per node. The results can then
        ///     be used
        ///     to start building each project individually, assuming a given project is built after its references.
        /// </remarks>
        /// <param name="entryProjectTargets">
        ///     The target list for the <see cref="GraphRoots" />. May be null or empty, in which case the entry projects' default
        ///     targets will be used.
        /// </param>
        /// <returns>A dictionary containing the target list for each node.</returns>
        public IReadOnlyDictionary<ProjectGraphNode, ImmutableList<string>> GetTargetLists(ICollection<string> entryProjectTargets)
        {
            // Seed the dictionary with empty lists for every node. In this particular case though an empty list means "build nothing" rather than "default targets".
            var targetLists = ProjectNodes.ToDictionary(node => node, node => ImmutableList<string>.Empty);

            var encounteredEdges = new HashSet<ProjectGraphBuildRequest>();
            var edgesToVisit = new Queue<ProjectGraphBuildRequest>();

            // Initial state for the graph roots
            foreach (var entryPointNode in GraphRoots)
            {
                var entryTargets = entryProjectTargets == null || entryProjectTargets.Count == 0
                    ? ImmutableList.CreateRange(entryPointNode.ProjectInstance.DefaultTargets)
                    : ImmutableList.CreateRange(entryProjectTargets);
                var entryEdge = new ProjectGraphBuildRequest(entryPointNode, entryTargets);
                encounteredEdges.Add(entryEdge);
                edgesToVisit.Enqueue(entryEdge);
            }

            // Traverse the entire graph, visiting each edge once.
            while (edgesToVisit.Count > 0)
            {
                var buildRequest = edgesToVisit.Dequeue();
                var node = buildRequest.Node;
                var requestedTargets = buildRequest.RequestedTargets;

                targetLists[node] = targetLists[node].AddRange(requestedTargets);

                // No need to continue if this node has no project references.
                if (node.ProjectReferences.Count == 0)
                {
                    continue;
                }

                // Based on the entry points of this project, determine which targets to propagate down to project references.
                var targetsToPropagate = ProjectInterpretation.TargetsToPropagate.FromProjectAndEntryTargets(node.ProjectInstance, requestedTargets);

                // Queue the project references for visitation, if the edge hasn't already been traversed.
                foreach (var referenceNode in node.ProjectReferences)
                {
                    var applicableTargets = targetsToPropagate.GetApplicableTargetsForReference(referenceNode.ProjectInstance);

                    var expandedTargets = ExpandDefaultTargets(
                        applicableTargets,
                        referenceNode.ProjectInstance.DefaultTargets,
                        Edges[(node, referenceNode)]);

                    var projectReferenceEdge = new ProjectGraphBuildRequest(
                        referenceNode,
                        expandedTargets);

                    if (encounteredEdges.Add(projectReferenceEdge))
                    {
                        edgesToVisit.Enqueue(projectReferenceEdge);
                    }
                }
            }

            // Dedupe target lists
            var entriesToUpdate = new List<KeyValuePair<ProjectGraphNode, ImmutableList<string>>>();
            foreach (var pair in targetLists)
            {
                var targetList = pair.Value;

                var seenTargets = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                var i = 0;
                while (i < targetList.Count)
                {
                    if (seenTargets.Add(targetList[i]))
                    {
                        i++;
                    }
                    else
                    {
                        targetList = targetList.RemoveAt(i);
                    }
                }

                // Only update if it changed
                if (targetList != pair.Value)
                {
                    entriesToUpdate.Add(new KeyValuePair<ProjectGraphNode, ImmutableList<string>>(pair.Key, targetList));
                }
            }

            // Update in a separate pass to avoid modifying a collection while iterating it.
            foreach (var pair in entriesToUpdate)
            {
                targetLists[pair.Key] = pair.Value;
            }

            return targetLists;
        }

        private static ImmutableList<string> ExpandDefaultTargets(ImmutableList<string> targets, List<string> defaultTargets, ProjectItemInstance graphEdge)
        {
            var i = 0;
            while (i < targets.Count)
            {
                if (targets[i].Equals(MSBuildConstants.DefaultTargetsMarker, StringComparison.OrdinalIgnoreCase))
                {
                    targets = targets
                        .RemoveAt(i)
                        .InsertRange(i, defaultTargets);
                    i += defaultTargets.Count;
                }
                else if (targets[i].Equals(MSBuildConstants.ProjectReferenceTargetsOrDefaultTargetsMarker, StringComparison.OrdinalIgnoreCase))
                {
                    var targetsString = graphEdge.GetMetadataValue(ItemMetadataNames.ProjectReferenceTargetsMetadataName);

                    var expandedTargets = string.IsNullOrEmpty(targetsString)
                        ? defaultTargets
                        : ExpressionShredder.SplitSemiColonSeparatedList(targetsString).ToList();

                    targets = targets
                        .RemoveAt(i)
                        .InsertRange(i, expandedTargets);

                    i += expandedTargets.Count;
                }
                else
                {
                    i++;
                }
            }

            return targets;
        }

        internal static ProjectInstance DefaultProjectInstanceFactory(
            string projectPath,
            Dictionary<string, string> globalProperties,
            ProjectCollection projectCollection)
        {
            return new ProjectInstance(
                projectPath,
                globalProperties,
                MSBuildConstants.CurrentToolsVersion,
                projectCollection);
        }

        private struct ProjectGraphBuildRequest : IEquatable<ProjectGraphBuildRequest>
        {
            public ProjectGraphBuildRequest(ProjectGraphNode node, ImmutableList<string> targets)
            {
                Node = node ?? throw new ArgumentNullException(nameof(node));
                RequestedTargets = targets ?? throw new ArgumentNullException(nameof(targets));
            }

            public ProjectGraphNode Node { get; }

            public ImmutableList<string> RequestedTargets { get; }

            public bool Equals(ProjectGraphBuildRequest other)
            {
                if (Node != other.Node
                    || RequestedTargets.Count != other.RequestedTargets.Count)
                {
                    return false;
                }

                // Target order is important
                for (var i = 0; i < RequestedTargets.Count; i++)
                {
                    if (!RequestedTargets[i].Equals(other.RequestedTargets[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }

            public override bool Equals(object obj)
            {
                return !ReferenceEquals(null, obj) && obj is ProjectGraphBuildRequest graphNodeWithTargets && Equals(graphNodeWithTargets);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    const int salt = 397;
                    var hashCode = Node.GetHashCode() * salt;
                    for (var i = 0; i < RequestedTargets.Count; i++)
                    {
                        hashCode *= salt;
                        hashCode ^= StringComparer.OrdinalIgnoreCase.GetHashCode(RequestedTargets[i]);
                    }

                    return hashCode;
                }
            }
        }
    }
}
