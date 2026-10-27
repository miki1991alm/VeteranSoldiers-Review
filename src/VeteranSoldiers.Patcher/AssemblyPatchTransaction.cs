using System;
using System.IO;
using Mono.Cecil;

namespace VeteranSoldiers.Patching
{
    /// <summary>Commits a serialized candidate only after the complete patch succeeds.</summary>
    public static class AssemblyPatchTransaction
    {
        public static bool TryApply(ref AssemblyDefinition assembly, Action<AssemblyDefinition> patch, out Exception? error)
        {
            AssemblyDefinition? candidate = null;
            AssemblyDefinition? verified = null;
            try
            {
                // Clone the incoming assembly, not the disk original: earlier preloaders own
                // their changes. Serialization may normalize Cecil offsets/tokens but does not
                // remove or replace any incoming member or instruction.
                candidate = Copy(assembly);
                patch(candidate);
                // Exercise Cecil's writer and reader before committing. A serialization failure
                // must not expose a partially instrumented assembly to subsequent preloaders.
                verified = Copy(candidate);
                assembly = verified;
                verified = null; // Ownership transfers to BepInEx's assembly pipeline.
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = exception;
                return false;
            }
            finally
            {
                candidate?.Dispose();
                verified?.Dispose();
                // Never dispose the incoming assembly: its owner and other preloaders may
                // still hold references, even when the candidate has been accepted.
            }
        }

        private static AssemblyDefinition Copy(AssemblyDefinition source)
        {
            var bytes = new MemoryStream();
            try
            {
                source.Write(bytes);
                bytes.Position = 0;
                // This Cecil build lazily reads method bodies even in Immediate mode and
                // does not duplicate caller-supplied streams for InMemory=true. Keep the
                // managed-only buffer alive through the module's reader; closing it here
                // would break this patch or a later preloader. It has no file handle.
                return AssemblyDefinition.ReadAssembly(bytes, new ReaderParameters {
                    AssemblyResolver = source.MainModule.AssemblyResolver,
                    InMemory = true,
                    ReadingMode = ReadingMode.Immediate,
                    ReadSymbols = false
                });
            }
            catch
            {
                bytes.Dispose();
                throw;
            }
        }
    }
}
