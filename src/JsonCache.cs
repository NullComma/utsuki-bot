using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

public static class JsonCache {

	#region <<---------- Properties ---------->>

	const string ROOT_FOLDER = "JsonData/";

	public static readonly JsonSerializerSettings DefaultSerializer = new() {
		Culture = CultureInfo.InvariantCulture,
		MissingMemberHandling = MissingMemberHandling.Ignore,
		NullValueHandling = NullValueHandling.Ignore,
		ReferenceLoopHandling = ReferenceLoopHandling.Ignore
	};
	
	#endregion <<---------- Properties ---------->>

	
	
	
	#region <<---------- Public ---------->>
	
	/// <summary>
	/// Serialize and save to file an object.
	/// </summary>
	/// <returns>Returns TRUE if success.</returns>
	public static bool SaveToJson<T>(string filePath, T @object) {
		try {
			if (string.IsNullOrEmpty(filePath)) return false;

			var filePathWithExtension = $"{Path.Combine(ROOT_FOLDER, filePath)}.json";

			// create folder
			Directory.CreateDirectory(Path.GetDirectoryName(filePathWithExtension));

			var jsonObject = JsonConvert.SerializeObject(@object, Formatting.Indented, DefaultSerializer);
		
			// write
			using (var writer = File.CreateText(filePathWithExtension)) {
				Console.WriteLine($"Writing file: {filePathWithExtension.Replace(ROOT_FOLDER, string.Empty)}");
				writer.Write(jsonObject);
			}
			return true;
		} catch (Exception e) {
			Console.WriteLine(e);
		}
		return false;
	}

	public static T LoadFromJson<T>(string filePath, TimeSpan maxCacheAge = default){
		try {
			var jsonString = LoadJsonString(filePath, maxCacheAge);
			if (string.IsNullOrEmpty(jsonString)) return default;
			return JsonConvert.DeserializeObject<T>(jsonString, DefaultSerializer);
		} catch (Exception e) {
			Console.WriteLine(e);
		}
		return default;
	}

	public static bool Delete(string path) {
		path = path.Replace('\\', '/').Trim();
		try {
			var filePath = $"{Path.Combine(ROOT_FOLDER, path)}.json";
			if (!File.Exists(filePath)) return false;
			File.Delete(filePath);
			return true;
		} catch (Exception e) {
			Console.WriteLine(e);
		}
		return false;
	}

	/// <summary>
	/// Returns TRUE if the folder is deleted.
	/// </summary>
	public static bool DeleteFolder(string folderPath) {
		try {
			folderPath = Path.GetDirectoryName($"{ROOT_FOLDER}{folderPath}");
			bool isInvalid = Path.GetInvalidPathChars().Any(folderPath.Contains);

			if (isInvalid || string.IsNullOrEmpty(folderPath) || string.IsNullOrWhiteSpace(folderPath)) return false;

			if (Directory.Exists(folderPath)) {
				Directory.Delete(folderPath, true);
				return Directory.Exists(folderPath);
			}
		} catch (Exception e) {
			Console.WriteLine($"Exception at {nameof(DeleteFolder)}: \n{e}");
		}
		return false;
	}
	
	public static List<T> GetAllJsonInsideFolder<T>(string directoryPath, bool recursive = false, string fileName = "") {
		try {
			var jsonList = new List<T>();
			directoryPath = Path.Combine(ROOT_FOLDER, directoryPath);
			if (!Directory.Exists(directoryPath)) return default;
			var filesPath = Directory.GetFiles(directoryPath, $"{fileName}*.json", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly);
			foreach (var completeFilePath in filesPath) {
				var filePath = completeFilePath.Substring(ROOT_FOLDER.Length).Replace(".json", string.Empty);
				if (string.IsNullOrEmpty(filePath)) continue;
				filePath = filePath.Replace('\\', '/');
				var json = LoadFromJson<T>(filePath);
				if (json != null) jsonList.Add(json);
			}
			return jsonList;
		} catch (Exception e) {
			Console.WriteLine($"Exception at {nameof(GetAllJsonInsideFolder)}:\n{e}");
		}
		return default;
	}
	
	#endregion <<---------- Public ---------->>

	
	
	
	#region <<---------- Private ---------->>

	static string LoadJsonString(string filePath, TimeSpan maxCacheAge = default) {
		filePath = filePath.Replace('\\', '/').Trim();
		try {
			var filePathWithExtension = $"{ROOT_FOLDER}{filePath}.json";
			if (!File.Exists(filePathWithExtension)) return null;

			if (maxCacheAge != default) {

				var info = new FileInfo(filePathWithExtension);
				var cacheAge = DateTime.UtcNow - info.LastWriteTimeUtc;
				if (cacheAge > maxCacheAge) {
					Console.WriteLine($"Deleting cache with age {cacheAge} from path '{filePathWithExtension}'");
					File.Delete(filePathWithExtension);
					return null;
				}
			}

			using var sourceStream = new FileStream(filePathWithExtension, FileMode.Open, FileAccess.Read, FileShare.Read, 4096);
			var sb = new StringBuilder();
			byte[] buffer = new byte[0x1000];
			int numRead = 0;
			while ((numRead = sourceStream.Read(buffer, 0, buffer.Length)) != 0) {
				sb.Append(Encoding.UTF8.GetString(buffer, 0, numRead));
			}
			return sb.ToString();
		} catch (Exception e) {
			Console.WriteLine(e);
		}
		return null;
	}
	
	#endregion <<---------- Private ---------->>
	
}
