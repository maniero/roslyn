﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Editor.UnitTests.Workspaces;
using Microsoft.CodeAnalysis.Remote;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Test.Utilities;
using Roslyn.Test.Utilities.Remote;
using Roslyn.VisualStudio.Next.UnitTests.Mocks;
using Xunit;

namespace Roslyn.VisualStudio.Next.UnitTests.Remote
{
    public class ServiceHubServicesTests
    {
        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public void TestRemoteHostCreation()
        {
            var remoteHostService = CreateService();
            Assert.NotNull(remoteHostService);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public void TestRemoteHostConnect()
        {
            var remoteHostService = CreateService();

            var input = "Test";
            var output = remoteHostService.Connect(input, serializedSession: null);

            Assert.Equal(input, output);
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestRemoteHostSynchronize()
        {
            var code = @"class Test { void Method() { } }";

            using (var workspace = await TestWorkspace.CreateCSharpAsync(code))
            {
                var client = (InProcRemoteHostClient)(await InProcRemoteHostClient.CreateAsync(workspace, runCacheCleanup: false, cancellationToken: CancellationToken.None));

                var solution = workspace.CurrentSolution;

                await UpdatePrimaryWorkspace(client, solution);
                VerifyAssetStorage(client, solution);

                Assert.Equal(
                    await solution.State.GetChecksumAsync(CancellationToken.None),
                    await PrimaryWorkspace.Workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None));
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestRemoteHostSynchronizeGlobalAssets()
        {
            var code = @"class Test { void Method() { } }";

            using (var workspace = await TestWorkspace.CreateCSharpAsync(code))
            {
                var client = (InProcRemoteHostClient)(await InProcRemoteHostClient.CreateAsync(workspace, runCacheCleanup: false, cancellationToken: CancellationToken.None));

                await client.RunOnRemoteHostAsync(
                    WellKnownRemoteHostServices.RemoteHostService,
                    workspace.CurrentSolution,
                    nameof(IRemoteHostService.SynchronizeGlobalAssetsAsync),
                    new object[] { new Checksum[0] { } }, CancellationToken.None);

                var storage = client.AssetStorage;
                Assert.Equal(0, storage.GetGlobalAssetsOfType<object>(CancellationToken.None).Count());
            }
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestUnknownProject()
        {
            var workspace = new AdhocWorkspace(TestHostServices.CreateHostServices());
            var solution = workspace.CurrentSolution.AddProject("unknown", "unknown", NoCompilationConstants.LanguageName).Solution;

            var client = (InProcRemoteHostClient)(await InProcRemoteHostClient.CreateAsync(workspace, runCacheCleanup: false, cancellationToken: CancellationToken.None));

            await UpdatePrimaryWorkspace(client, solution);
            VerifyAssetStorage(client, solution);

            Assert.Equal(
                await solution.State.GetChecksumAsync(CancellationToken.None),
                await PrimaryWorkspace.Workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None));
        }

        [Fact, Trait(Traits.Feature, Traits.Features.RemoteHost)]
        public async Task TestRemoteHostSynchronizeIncrementalUpdate()
        {
            using (var workspace = await TestWorkspace.CreateCSharpAsync(Array.Empty<string>(), metadataReferences: null))
            {
                var client = (InProcRemoteHostClient)(await InProcRemoteHostClient.CreateAsync(workspace, runCacheCleanup: false, cancellationToken: CancellationToken.None));

                var solution = Populate(workspace.CurrentSolution.RemoveProject(workspace.CurrentSolution.ProjectIds.First()));

                // verify initial setup
                await UpdatePrimaryWorkspace(client, solution);
                VerifyAssetStorage(client, solution);

                Assert.Equal(
                    await solution.State.GetChecksumAsync(CancellationToken.None),
                    await PrimaryWorkspace.Workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None));

                // incrementally update
                solution = await VerifyIncrementalUpdatesAsync(client, solution, csAddition: " ", vbAddition: " ");

                Assert.Equal(
                    await solution.State.GetChecksumAsync(CancellationToken.None),
                    await PrimaryWorkspace.Workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None));

                // incrementally update
                solution = await VerifyIncrementalUpdatesAsync(client, solution, csAddition: "\r\nclass Addition { }", vbAddition: "\r\nClass VB\r\nEnd Class");

                Assert.Equal(
                    await solution.State.GetChecksumAsync(CancellationToken.None),
                    await PrimaryWorkspace.Workspace.CurrentSolution.State.GetChecksumAsync(CancellationToken.None));
            }
        }

        private static async Task<Solution> VerifyIncrementalUpdatesAsync(InProcRemoteHostClient client, Solution solution, string csAddition, string vbAddition)
        {
            Assert.True(PrimaryWorkspace.Workspace is RemoteWorkspace);

            var remoteSolution = PrimaryWorkspace.Workspace.CurrentSolution;
            var projectIds = solution.ProjectIds;

            for (var i = 0; i < projectIds.Count; i++)
            {
                var projectName = $"Project{i}";
                var project = solution.GetProject(projectIds[i]);

                var documentIds = project.DocumentIds;
                for (var j = 0; j < documentIds.Count; j++)
                {
                    var documentName = $"Document{j}";

                    var currentSolution = UpdateSolution(solution, projectName, documentName, csAddition, vbAddition);
                    await UpdatePrimaryWorkspace(client, currentSolution);

                    var currentRemoteSolution = PrimaryWorkspace.Workspace.CurrentSolution;
                    VerifyStates(remoteSolution, currentRemoteSolution, projectName, documentName);

                    solution = currentSolution;
                    remoteSolution = currentRemoteSolution;

                    Assert.Equal(
                        await solution.State.GetChecksumAsync(CancellationToken.None),
                        await remoteSolution.State.GetChecksumAsync(CancellationToken.None));
                }
            }

            return solution;
        }

        private static void VerifyStates(Solution solution1, Solution solution2, string projectName, string documentName)
        {
            Assert.True(solution1.Workspace is RemoteWorkspace);
            Assert.True(solution2.Workspace is RemoteWorkspace);

            SetEqual(solution1.ProjectIds, solution2.ProjectIds);

            var (project, document) = GetProjectAndDocument(solution1, projectName, documentName);

            var projectId = project.Id;
            var documentId = document.Id;

            var projectIds = solution1.ProjectIds;
            for (var i = 0; i < projectIds.Count; i++)
            {
                var currentProjectId = projectIds[i];

                var projectStateShouldSame = projectId != currentProjectId;
                Assert.Equal(projectStateShouldSame, object.ReferenceEquals(solution1.GetProject(currentProjectId).State, solution2.GetProject(currentProjectId).State));

                if (!projectStateShouldSame)
                {
                    SetEqual(solution1.GetProject(currentProjectId).DocumentIds, solution2.GetProject(currentProjectId).DocumentIds);

                    var documentIds = solution1.GetProject(currentProjectId).DocumentIds;
                    for (var j = 0; j < documentIds.Count; j++)
                    {
                        var currentDocumentId = documentIds[j];

                        var documentStateShouldSame = documentId != currentDocumentId;
                        Assert.Equal(documentStateShouldSame, object.ReferenceEquals(solution1.GetDocument(currentDocumentId).State, solution2.GetDocument(currentDocumentId).State));
                    }
                }
            }
        }

        private static void VerifyAssetStorage(InProcRemoteHostClient client, Solution solution)
        {
            var map = solution.GetAssetMap();
            var storage = client.AssetStorage;

            object data;
            foreach (var kv in map)
            {
                Assert.True(storage.TryGetAsset(kv.Key, out data));
            }
        }

        private static Solution UpdateSolution(Solution solution, string projectName, string documentName, string csAddition, string vbAddition)
        {
            var (project, document) = GetProjectAndDocument(solution, projectName, documentName);

            return document.WithText(GetNewText(document, csAddition, vbAddition)).Project.Solution;
        }

        private static SourceText GetNewText(Document document, string csAddition, string vbAddition)
        {
            if (document.Project.Language == LanguageNames.CSharp)
            {
                return SourceText.From(document.State.GetTextSynchronously(CancellationToken.None).ToString() + csAddition);
            }

            return SourceText.From(document.State.GetTextSynchronously(CancellationToken.None).ToString() + vbAddition);
        }

        private static (Project, Document) GetProjectAndDocument(Solution solution, string projectName, string documentName)
        {
            var project = solution.Projects.First(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));
            var document = project.Documents.First(d => string.Equals(d.Name, documentName, StringComparison.OrdinalIgnoreCase));

            return (project, document);
        }

        private static async Task UpdatePrimaryWorkspace(InProcRemoteHostClient client, Solution solution)
        {
            await client.RunOnRemoteHostAsync(
                WellKnownRemoteHostServices.RemoteHostService, solution,
                nameof(IRemoteHostService.SynchronizePrimaryWorkspaceAsync),
                await solution.State.GetChecksumAsync(CancellationToken.None), CancellationToken.None);
        }

        private static Solution Populate(Solution solution)
        {
            solution = AddProject(solution, LanguageNames.CSharp, new[]
            {
                "class CS { }",
                "class CS2 { }"
            }, new[]
            {
                "cs additional file content"
            }, Array.Empty<ProjectId>());

            solution = AddProject(solution, LanguageNames.VisualBasic, new[]
            {
                "Class VB\r\nEnd Class",
                "Class VB2\r\nEnd Class"
            }, new[]
            {
                "vb additional file content"
            }, new ProjectId[] { solution.ProjectIds.First() });

            solution = AddProject(solution, LanguageNames.CSharp, new[]
            {
                "class Top { }"
            }, new[]
            {
                "cs additional file content"
            }, solution.ProjectIds.ToArray());

            solution = AddProject(solution, LanguageNames.CSharp, new[]
            {
                "class OrphanCS { }",
                "class OrphanCS2 { }"
            }, new[]
            {
                "cs additional file content",
                "cs additional file content2"
            }, Array.Empty<ProjectId>());

            return solution;
        }

        private static Solution AddProject(Solution solution, string language, string[] documents, string[] additionalDocuments, ProjectId[] p2pReferences)
        {
            var projectName = $"Project{solution.ProjectIds.Count}";
            var project = solution.AddProject(projectName, $"{projectName}.dll", language)
                                  .AddMetadataReference(MetadataReference.CreateFromFile(typeof(object).Assembly.Location))
                                  .AddAnalyzerReference(new AnalyzerFileReference(typeof(object).Assembly.Location, new TestAnalyzerAssemblyLoader()));

            var projectId = project.Id;
            solution = project.Solution;

            for (var i = 0; i < documents.Length; i++)
            {
                var current = solution.GetProject(projectId);
                solution = current.AddDocument($"Document{i}", SourceText.From(documents[i])).Project.Solution;
            }

            for (var i = 0; i < additionalDocuments.Length; i++)
            {
                var current = solution.GetProject(projectId);
                solution = current.AddAdditionalDocument($"AdditionalDocument{i}", SourceText.From(additionalDocuments[i])).Project.Solution;
            }

            for (var i = 0; i < p2pReferences.Length; i++)
            {
                var current = solution.GetProject(projectId);
                solution = current.AddProjectReference(new ProjectReference(p2pReferences[i])).Solution;
            }

            return solution;
        }

        private static RemoteHostService CreateService()
        {
            var stream = new MemoryStream();
            return new RemoteHostService(stream, new InProcRemoteHostClient.ServiceProvider(runCacheCleanup: false));
        }

        public static void SetEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual)
        {
            var expectedSet = new HashSet<T>(expected);
            var result = expected.Count() == actual.Count() && expectedSet.SetEquals(actual);
            if (!result)
            {
                Assert.True(result);
            }
        }
    }
}
