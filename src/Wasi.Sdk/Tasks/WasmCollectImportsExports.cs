﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Framework;
using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace Wasi.Sdk.Tasks;

/// <summary>
/// Scans a set of assemblies to locate import/export declarations, and generates the WASI SDK-compatible
/// C code to wire them up.
/// </summary>
public class WasmCollectImportsExports : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Assemblies { get; set; } = default!;

    public override bool Execute()
    {
        if (Assemblies!.Length == 0)
        {
            Log.LogError($"{nameof(WasmCollectImportsExports)}.{nameof(Assemblies)} cannot be empty");
            return false;
        }

        var resolver = new PathAssemblyResolver(Assemblies.Select(a => a.ItemSpec).ToList());
        using var metadataLoadContext = new MetadataLoadContext(resolver, "System.Private.CoreLib");
        var jsonOptions = AssemblyImportExportInfo.CreateSerializerOptions(metadataLoadContext);

        foreach (var assemblyItem in Assemblies)
        {
            // If the per-assembly generated file already exists, we can skip the assembly. The generated file path
            // includes a content hash, so its existence shows we're up-to-date
            var assemblyGeneratedFilePath = assemblyItem.GetMetadata("GeneratedSource")
                ?? throw new InvalidOperationException($"Item '{assemblyItem.ItemSpec}' lacks required metadata 'GeneratedSource'");
            if (File.Exists(assemblyGeneratedFilePath))
            {
                continue;
            }

            // Now call into the runtime's regular PInvokeTableGenerator to collect the per-assembly info
            var assemblyName = assemblyItem.ItemSpec;
            var assembly = metadataLoadContext.LoadFromAssemblyPath(assemblyName);
            var extractedInfo = new AssemblyImportExportInfo();
            foreach (var type in assembly.GetTypes())
            {
                PInvokeTableGenerator.CollectPInvokes(Log, extractedInfo.PInvokes, extractedInfo.PInvokeCallbacks, extractedInfo.Signatures, type);
            }

            // Finally, emit the per-assembly info into a file
            Directory.CreateDirectory(Path.GetDirectoryName(assemblyGeneratedFilePath)!);
            using var assemblyIntermediateFileStream = File.OpenWrite(assemblyGeneratedFilePath);
            if (!extractedInfo.IsEmpty)
            {
                JsonSerializer.Serialize(assemblyIntermediateFileStream, extractedInfo, jsonOptions);
            }
        }

        return true;
    }
}
