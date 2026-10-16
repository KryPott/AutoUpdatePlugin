using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;

namespace AutoUpdatingPlugin
{
	internal static class ModUpdater
	{
		public static int progressTotal = 0, progressDownload = 0;
		private static int toUpdateCount = 0;
		private static int updatedCount = 0;
		private static readonly List<FailedUpdateInfo> failedUpdates = new List<FailedUpdateInfo>();

		internal static bool pluginUpdated = false;

		//Added .modcomponent support
		private static bool IsModComponent(string path)
		{
			return string.Equals(Path.GetExtension(path), ".modcomponent", StringComparison.OrdinalIgnoreCase);
		}

		private static bool HasReplacementForComponent(string installedPath, List<(string, byte[]?)> downloadedData)
		{
			string installedName = Path.GetFileName(installedPath);
			foreach ((string path, byte[]? data) in downloadedData)
			{
				if (data == null || !IsModComponent(path)) continue;
				if (string.Equals(path, installedPath, StringComparison.OrdinalIgnoreCase) ||
					string.Equals(Path.GetFileName(path), installedName, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		private static void ExtractModComponentsFromArchives(List<(string, byte[]?)> downloadedData, InstalledModDetail installedMod)
		{
			List<string> existingComponents = installedMod.Files
				.Where(file => IsModComponent(file.filepath))
				.Select(file => file.filepath)
				.ToList();

			List<(string, byte[]?)> extractedComponents = new List<(string, byte[]?)>();
			foreach ((string archivePath, byte[]? data) in downloadedData.ToList())
			{
				if (data == null || !string.Equals(Path.GetExtension(archivePath), ".zip", StringComparison.OrdinalIgnoreCase))
					continue;

				try
				{
					using MemoryStream stream = new MemoryStream(data);
					using ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read);
					List<ZipArchiveEntry> entries = archive.Entries
						.Where(entry => !string.IsNullOrEmpty(entry.Name) && IsModComponent(entry.FullName))
						.ToList();

					foreach (ZipArchiveEntry entry in entries)
					{
						string entryName = Path.GetFileName(entry.FullName);
						string? destination = existingComponents.FirstOrDefault(path =>
							string.Equals(Path.GetFileName(path), entryName, StringComparison.OrdinalIgnoreCase));

						if (destination == null && entries.Count == 1 && existingComponents.Count == 1)
							destination = existingComponents[0];
						if (destination == null && entries.Count == 1)
							destination = Path.Combine(Path.GetDirectoryName(archivePath) ?? string.Empty, entryName);
						if (string.IsNullOrEmpty(destination))
						{
							Logger.Warning($"Could not determine destination for .modcomponent in {archivePath}; keeping existing component files.");
							continue;
						}

						bool directReplacementExists = downloadedData.Any(file => file.Item2 != null && IsModComponent(file.Item1) &&
							string.Equals(Path.GetFileName(file.Item1), Path.GetFileName(destination), StringComparison.OrdinalIgnoreCase));
						if (directReplacementExists) continue;

						using Stream entryStream = entry.Open();
						using MemoryStream output = new MemoryStream();
						entryStream.CopyTo(output);
						extractedComponents.Add((destination, output.ToArray()));
					}
				}
				catch (Exception e)
				{
					Logger.Debug($"Could not extract .modcomponent files from {archivePath}: {e.Message}");
				}
			}

			foreach ((string path, byte[]? data) in extractedComponents)
			{
				int existingIndex = downloadedData.FindIndex(file =>
					string.Equals(file.Item1, path, StringComparison.OrdinalIgnoreCase));
				if (existingIndex >= 0) downloadedData[existingIndex] = (path, data);
				else downloadedData.Add((path, data));
			}
		}

		private static void LogProgress()
		{
			if (toUpdateCount == 0)
			{
				Logger.Success("All installed mods are already up to date !");
			}
			if (failedUpdates.Count > 0)
			{
				Logger.Warning($"{failedUpdates.Count} mods failed to update ({toUpdateCount - failedUpdates.Count}/{toUpdateCount} succeeded)");
				foreach (FailedUpdateInfo f in failedUpdates)
				{
					Logger.Warning($"- {f.mod.CleanName} >> {f.reason} >> {f.message}");
				}
			}
			if(updatedCount > 0)
			{
				Logger.Success("Successfully updated " + updatedCount + " mods !");
			}
		}
		internal static void DownloadAndUpdateMods()
		{
//			Logger.Minor("Checking for outdated mods...");
			List<Tuple<InstalledModDetail, APIMod>> toUpdate = new List<Tuple<InstalledModDetail, APIMod>>();
			// List all installed mods that can be updated
			foreach (KeyValuePair<InstalledModDetail, APIMod> pair in IntersectedList.installedApiMods)
			{
				InstalledModDetail? installedMod = pair.Key;
				APIMod? remoteMod = pair.Value;
				if (installedMod.Outdated)
				{
					toUpdate.Add(new Tuple<InstalledModDetail, APIMod>(installedMod, remoteMod));
				}
				else if (installedMod.CanBeUpdated())
				{
					VersionData installedModVersion = installedMod.GetMinValidVersion();
					VersionData remoteModVersion = remoteMod.VersionData;
#if DEBUG
					int compareResult = remoteModVersion.CompareTo(installedModVersion);
					Logger.Debug($"Version comparison between [remote] {remoteMod.Version} and [local] {installedModVersion.ToString(3)} for ({remoteMod.CleanName}): " + compareResult);
#endif
					if (installedModVersion < remoteModVersion)
					{
						toUpdate.Add(new Tuple<InstalledModDetail, APIMod>(installedMod, remoteMod));
					}
				}
			}
			toUpdateCount = toUpdate.Count;

			if (toUpdateCount > 0)
			{
				Logger.Warning($"Found {toUpdateCount} outdated mods.");
			}

			for (int i = 0; i < toUpdateCount; ++i)
			{
				int ii = i + 1;
				InstalledModDetail installedMod = toUpdate[i].Item1;
				APIMod apiMod = toUpdate[i].Item2;

				if(APIList.IsDisabled(apiMod.CleanName))
				{
					failedUpdates.Add(new FailedUpdateInfo(installedMod, FailedUpdateReason.Disabled, APIList.GetDisabledReason(apiMod.CleanName) ?? ""));
					continue;
				}
				Logger.Minor($"Updating '{installedMod.CleanName}' ({ii} / {toUpdateCount})...");
				progressTotal = (int)(i / (double)toUpdateCount * 100);

				UpdateInstallation(installedMod, apiMod);

				progressTotal = (int)((ii) / (double)toUpdateCount * 100);
				Logger.Debug($"Progress: {ii}/{toUpdateCount} -> {progressTotal}%");
				updatedCount++;

			}

			LogProgress();
		}
		public static void UpdateInstallation(InstalledModDetail installedMod, APIMod apiMod)
		{
			try
			{
				bool errored = false;
				List<(string, byte[]?)> downloadedData = new();
				using WebClient client = new();
				bool downloading;
				byte[]? buffer;
				client.DownloadDataCompleted += (sender, e) =>
				{
					if (e.Error != null)
					{
						Logger.Error("Failed to download " + installedMod.CleanName + ":\n" + e.Error);
						errored = true;
						failedUpdates.Add(new FailedUpdateInfo(installedMod, FailedUpdateReason.DownloadError, e.ToString() ?? ""));
					}
					else
					{
						buffer = e.Result;
					}
					downloading = false;
				};
				foreach (string link in apiMod.Downloads)
				{
					downloading = true;
					buffer = null;
					client.DownloadDataAsync(new Uri(link));

					while (downloading)
					{
						Thread.Sleep(50);
					}

					if (apiMod.Type == "plugin")
					{
						pluginUpdated = true;
						downloadedData.Add((FileUtils.GetDestinationPlugin(link), buffer));
					} else {
						downloadedData.Add((FileUtils.GetDestination(link), buffer));
					}
				}

				if (!errored)
				{
					ExtractModComponentsFromArchives(downloadedData, installedMod);
					try
					{
						foreach (InstalledFileDetail oldFile in installedMod.Files)
						{
							if (IsModComponent(oldFile.filepath) && !HasReplacementForComponent(oldFile.filepath, downloadedData))
							{
								Logger.Debug($"Keeping existing .modcomponent file because no replacement was downloaded: {oldFile.filepath}");
								continue;
							}
							try
							{
	//								Logger.Msg($"Deleting {oldFile.filepath}");
								File.Delete(oldFile.filepath);
							} catch (Exception e)
							{
	//								Logger.Msg($"NOPE! Deleting {oldFile.filepath}, lets rename it instead");
								File.Move(oldFile.filepath,oldFile.filepath + ".rem");
							}
						}
						foreach ((string, byte[]?) modFile in downloadedData)
						{
							Logger.Msg($"Writing to {modFile.Item1}");
							File.WriteAllBytes(modFile.Item1, modFile.Item2);
						}
					}
					catch (Exception e)
					{
						Logger.Error("Failed to save replacement files for " + installedMod.CleanName + ":\n" + e);
						failedUpdates.Add(new FailedUpdateInfo(installedMod, FailedUpdateReason.SaveError, e.ToString()));
						return;
					}
				}
			}
			catch (Exception e)
			{
				Logger.Error("Failed to update " + installedMod.CleanName + ":\n" + e);
				failedUpdates.Add(new FailedUpdateInfo(installedMod, FailedUpdateReason.Unknown, e.ToString()));
			}

		}
	}
}
