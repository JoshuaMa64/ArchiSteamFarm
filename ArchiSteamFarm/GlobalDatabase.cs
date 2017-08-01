﻿/*
    _                _      _  ____   _                           _____
   / \    _ __  ___ | |__  (_)/ ___| | |_  ___   __ _  _ __ ___  |  ___|__ _  _ __  _ __ ___
  / _ \  | '__|/ __|| '_ \ | |\___ \ | __|/ _ \ / _` || '_ ` _ \ | |_  / _` || '__|| '_ ` _ \
 / ___ \ | |  | (__ | | | || | ___) || |_|  __/| (_| || | | | | ||  _|| (_| || |   | | | | | |
/_/   \_\|_|   \___||_| |_||_||____/  \__|\___| \__,_||_| |_| |_||_|   \__,_||_|   |_| |_| |_|

 Copyright 2015-2017 Łukasz "JustArchi" Domeradzki
 Contact: JustArchi@JustArchi.net

 Licensed under the Apache License, Version 2.0 (the "License");
 you may not use this file except in compliance with the License.
 You may obtain a copy of the License at

 http://www.apache.org/licenses/LICENSE-2.0
					
 Unless required by applicable law or agreed to in writing, software
 distributed under the License is distributed on an "AS IS" BASIS,
 WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 See the License for the specific language governing permissions and
 limitations under the License.

*/

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace ArchiSteamFarm {
	internal sealed class GlobalDatabase : IDisposable {
		[JsonProperty(Required = Required.DisallowNull)]
		internal readonly ConcurrentDictionary<uint, ConcurrentHashSet<uint>> AppIDsToPackageIDs = new ConcurrentDictionary<uint, ConcurrentHashSet<uint>>();

		[JsonProperty(Required = Required.DisallowNull)]
		internal readonly Guid Guid = Guid.NewGuid();

		[JsonProperty(Required = Required.DisallowNull)]
		internal readonly InMemoryServerListProvider ServerListProvider = new InMemoryServerListProvider();

		private readonly object FileLock = new object();

		private readonly SemaphoreSlim PackagesRefreshSemaphore = new SemaphoreSlim(1, 1);

		internal uint CellID {
			get => _CellID;
			set {
				if ((value == 0) || (_CellID == value)) {
					return;
				}

				_CellID = value;
				Save();
			}
		}

		[JsonProperty(Required = Required.DisallowNull)]
		private uint _CellID;

		private string FilePath;

		// This constructor is used when creating new database
		private GlobalDatabase(string filePath) : this() {
			if (string.IsNullOrEmpty(filePath)) {
				throw new ArgumentNullException(nameof(filePath));
			}

			FilePath = filePath;
			Save();
		}

		// This constructor is used only by deserializer
		private GlobalDatabase() => ServerListProvider.ServerListUpdated += OnServerListUpdated;

		public void Dispose() => ServerListProvider.ServerListUpdated -= OnServerListUpdated;

		internal static GlobalDatabase Load(string filePath) {
			if (string.IsNullOrEmpty(filePath)) {
				ASF.ArchiLogger.LogNullError(nameof(filePath));
				return null;
			}

			if (!File.Exists(filePath)) {
				return new GlobalDatabase(filePath);
			}

			GlobalDatabase globalDatabase;

			try {
				globalDatabase = JsonConvert.DeserializeObject<GlobalDatabase>(File.ReadAllText(filePath));
			} catch (Exception e) {
				ASF.ArchiLogger.LogGenericException(e);
				return null;
			}

			if (globalDatabase == null) {
				ASF.ArchiLogger.LogNullError(nameof(globalDatabase));
				return null;
			}

			globalDatabase.FilePath = filePath;
			return globalDatabase;
		}

		internal async Task RefreshPackageIDs(Bot bot, ICollection<uint> packageIDs) {
			if ((bot == null) || (packageIDs == null) || (packageIDs.Count == 0)) {
				ASF.ArchiLogger.LogNullError(nameof(bot) + " || " + nameof(packageIDs));
				return;
			}

			await PackagesRefreshSemaphore.WaitAsync().ConfigureAwait(false);

			try {
				HashSet<uint> missingPackageIDs = new HashSet<uint>(packageIDs.AsParallel().Where(packageID => AppIDsToPackageIDs.Values.All(packages => !packages.Contains(packageID))));
				if (missingPackageIDs.Count == 0) {
					return;
				}

				Dictionary<uint, HashSet<uint>> appIDsToPackageIDs = await bot.GetAppIDsToPackageIDs(missingPackageIDs);
				if ((appIDsToPackageIDs == null) || (appIDsToPackageIDs.Count == 0)) {
					return;
				}

				foreach (KeyValuePair<uint, HashSet<uint>> appIDtoPackageID in appIDsToPackageIDs) {
					if (!AppIDsToPackageIDs.TryGetValue(appIDtoPackageID.Key, out ConcurrentHashSet<uint> packages)) {
						packages = new ConcurrentHashSet<uint>();
						AppIDsToPackageIDs[appIDtoPackageID.Key] = packages;
					}

					foreach (uint package in appIDtoPackageID.Value) {
						packages.Add(package);
					}
				}

				Save();
			} finally {
				PackagesRefreshSemaphore.Release();
			}
		}

		private void OnServerListUpdated(object sender, EventArgs e) => Save();

		private void Save() {
			string json = JsonConvert.SerializeObject(this);
			if (string.IsNullOrEmpty(json)) {
				ASF.ArchiLogger.LogNullError(nameof(json));
				return;
			}

			lock (FileLock) {
				string newFilePath = FilePath + ".new";

				try {
					File.WriteAllText(newFilePath, json);

					if (File.Exists(FilePath)) {
						File.Replace(newFilePath, FilePath, null);
					} else {
						File.Move(newFilePath, FilePath);
					}
				} catch (Exception e) {
					ASF.ArchiLogger.LogGenericException(e);
				}
			}
		}
	}
}