using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Spotlight.Storage
{
    /// <summary>
    /// Tracks archive contents read, written, or explicitly declined for reloading. Timestamps
    /// can be rounded, ahead of the clock, or preserved by external tools. Each successful write
    /// is acknowledged immediately, even if a later file's save fails. Save/check scopes prevent
    /// WinForms activation events from reopening prompts mid-operation. Used on the UI thread.
    /// </summary>
    public sealed class ArchiveChangeTracker
    {
        private readonly Dictionary<string, byte[]> fingerprints = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private bool saving;
        private bool checking;

        public byte[] ReadFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Acknowledge(path, bytes);
            return bytes;
        }

        public void WriteFile(string path, byte[] bytes)
        {
            try { File.WriteAllBytes(path, bytes); }
            catch (IOException ex)
            {
                throw new IOException("Could not write stage archive:\n" + Path.GetFullPath(path) + "\n\n" + ex.Message +
                    "\n\nIf the game is running, stop emulation and retry Save. Leave Spotlight open to retain unsaved edits.", ex);
            }
            Acknowledge(path, bytes);
        }

        public void Acknowledge(string path, byte[] bytes)
        {
            fingerprints[Path.GetFullPath(path)] = Fingerprint(bytes);
        }

        /// <summary>Returns a snapshot; another edit during the reload dialog remains detectable.</summary>
        public bool TryReadExternalChange(string path, out byte[] bytes)
        {
            bytes = null;
            if (saving) return false;
            byte[] current;
            try { current = File.ReadAllBytes(path); }
            // Deleted/locked archives cannot be reloaded; retain the baseline and retry next activation.
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            if (fingerprints.TryGetValue(Path.GetFullPath(path), out byte[] known) && known.SequenceEqual(Fingerprint(current)))
                return false;
            bytes = current;
            return true;
        }

        public bool RunSave(Func<bool> save)
        {
            if (saving) return false;
            saving = true;
            try { return save(); }
            finally { saving = false; }
        }

        public void RunCheck(Action check)
        {
            if (saving || checking) return;
            checking = true;
            try { check(); }
            finally { checking = false; }
        }

        private static byte[] Fingerprint(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(bytes);
        }
    }
}
