﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEditor;
using UnityEditor.AnimatedValues;
using UnityEditor.Callbacks;
using UnityEngine;

namespace GitRepositoryManager
{
	public class RepoManagerWindow : EditorWindow
	{
		private static RepoManagerWindow _window;

		[Serializable]
		private class DependencyInfo
		{
			public List<Dependency> Dependencies = new List<Dependency>();
		}

		private string _repoPath;
		private string _rootDependenciesFile;
		private DependencyInfo _dependencies;
		private Dependency _potentialNewDependency = new Dependency();
		//	private int selectedFilterIndex;

		private AnimBool _showAddDependencyMenu;

		private List<RepoPanel> _repoPanels;

		private RepositoryTester _tester;

		private double _addTime;
		private float _minimumAddTimeBeforeFeedback = 0.25f;
		private string addDependencyFailureMessage;
		private bool showWarningMessage;
		private bool lastFrameWasWaitingToShowWarning;

		private const string FULL_RE_IMPORT_KEY = "RepositoryManager.FullReImport";

		private HashSet<RepoPanel> _reposWereBusy = new HashSet<RepoPanel>();
		private HashSet<RepoPanel> _reposBusy = new HashSet<RepoPanel>();

		[MenuItem("Window/Repository Manager", priority = 1500)]
		static void Init()
		{
			//Find other windows to dock to by default.
			List<Type> types = new List<Type>();
			EditorWindow[] allWindows = Resources.FindObjectsOfTypeAll<EditorWindow>();
			foreach (EditorWindow window in allWindows)
			{
				//Can see the project view still in most configurations, and the scene tree is shown vertically. alternatives could be project view or inspector.
				if (window.GetType().Name == "SceneHierarchyWindow")
				{
					types.Add(window.GetType());
				}
			}

			_window = (RepoManagerWindow)GetWindow<RepoManagerWindow>(types.ToArray());
			_window.titleContent = new GUIContent("Repository Manager");
			_window.Show();
		}

		private void UpdateDependencies(List<Dependency> updatedDependencies = null)
		{
			_repoPath = Path.Combine(Application.dataPath, "Repositories");
			_rootDependenciesFile = Path.Combine(_repoPath, "Dependencies.json");

			//Ensure to create directory structure and default dependencies if nothing exists
			Directory.CreateDirectory(_repoPath);
			if (!File.Exists(_rootDependenciesFile)) { File.WriteAllText(_rootDependenciesFile, JsonUtility.ToJson(new DependencyInfo(), true)); }
			string json = File.ReadAllText(_rootDependenciesFile);
			_dependencies = JsonUtility.FromJson<DependencyInfo>(json);

			//Sync file dependencies with in memory dependencies
			if (updatedDependencies != null)
			{
				//Remove no longer existing
				for (int i = _dependencies.Dependencies.Count - 1; i >= 0; i--)
				{
					var dep = _dependencies.Dependencies[i];
					if (updatedDependencies.FindAll(d => d.Url == dep.Url).Count <= 0)
					{
						_dependencies.Dependencies.RemoveAt(i);
					}
				}

				//Add new
				foreach (var dep in updatedDependencies)
				{
					if (_dependencies.Dependencies.FindAll(d => d.Url == dep.Url).Count <= 0)
					{
						_dependencies.Dependencies.Add(dep);
					}
				}

				json = JsonUtility.ToJson(_dependencies, true);
				File.WriteAllText(_rootDependenciesFile, json);
			}

			//Update repo panels
			_repoPanels = new List<RepoPanel>();
			foreach (Dependency dependency in _dependencies.Dependencies)
			{
				if (_repoPanels.FindAll(p => dependency.Url == p.DependencyInfo.Url).Count == 0)
				{
					RepoPanel panel = new RepoPanel(_repoPath, dependency);
					panel.OnRemovalRequested += OnPanelRemovalRequested;
					panel.OnDeleteAssetsRequested += DeleteAssets;
					panel.OnCopyFinished += (assets, updatedRepos) =>  //TODO: never used?
					{
						UpdateAssetDatabaseForNewAssets(assets, updatedRepos);
					};
					_repoPanels.Add(panel);
				}
			}
			Repaint();
		}

		//This will be called after a copy where the files were found in the directories explored by the copy, but never replaced.
		//This means they are local only strays that should be removed.
		private void DeleteAssets(List<string> toDelete)
		{
			for (int i = 0; i < toDelete.Count; i++)
			{
				string extension = Path.GetExtension(toDelete[i]);
				if (extension == ".meta")
				{
					//dont delete meta files directly.
					continue;
				}

				string assetDBPath = GetAssetDatabasePathFromFullPath(toDelete[i]);

				EditorUtility.DisplayProgressBar("Cleaning Repositories", "Removing stray files. Please wait a moment" + GUIUtility.GetLoadingDots(), ((float)i) / toDelete.Count);
				AssetDatabase.DeleteAsset(assetDBPath);
				//Debug.Log("Deleting" + assetDBPath);

				string toDeleteLocal = toDelete[i];

				//Delete asset does not happen straight away.
				//TODO: this is not recursing properly. File still exists.
				string deleteDirectory = Path.GetDirectoryName(toDeleteLocal);
				if (Directory.GetFiles(deleteDirectory).Length == 0 && Directory.GetDirectories(deleteDirectory).Length == 0)
				{
					Directory.Delete(deleteDirectory);
					//Debug.Log("Deleting directory " + deleteDirectory);
					//Recurse upwards deleting any directories with no files or folders.
					DirectoryInfo parentDir = Directory.GetParent(deleteDirectory);
					int filesCount = Directory.GetFiles(parentDir.FullName).Length;
					int directoryCount = Directory.GetDirectories(parentDir.FullName).Length;
					while (directoryCount == 0 && filesCount == 0)
					{
						Directory.Delete(parentDir.FullName);
						//Debug.Log("Deleting directory " + parentDir.FullName);
						parentDir = Directory.GetParent(parentDir.FullName);
						//Debug.Log("Evaluating next directory " + parentDir.FullName);
					}
				}
			}
		}

		private void UpdateAssetDatabaseForNewAssets(List<string> coppiedAssets, params RepoPanel[] updatedRepos)
		{
			EditorUtility.DisplayProgressBar("Importing Repositories", "Performing full re-import" + GUIUtility.GetLoadingDots(), (float)EditorApplication.timeSinceStartup % 1);
			AssetDatabase.Refresh();

			//snapshot folder and file state to compare against later!
			foreach (RepoPanel panel in updatedRepos)
			{
				panel.TakeBaselineSnapshot();
			}

			EditorUtility.ClearProgressBar();
		}

		private string GetAssetDatabasePathFromFullPath(string fullPath)
		{
			fullPath = fullPath.Replace('\\', '/');
			string assetDBPath = fullPath.Replace(Application.dataPath, "");

			if (Path.IsPathRooted(assetDBPath))
			{
				assetDBPath = assetDBPath.TrimStart(Path.DirectorySeparatorChar);
				assetDBPath = assetDBPath.TrimStart(Path.AltDirectorySeparatorChar);
			}

			return Path.Combine("Assets", assetDBPath);
		}

		private void OnPanelRemovalRequested(string name, string url, string repoPath)
		{
			for (int i = 0; i < _dependencies.Dependencies.Count; i++)
			{
				if (_dependencies.Dependencies[i].Name == name && _dependencies.Dependencies[i].Url == url)
				{
					RepoPanel panel = _repoPanels.Find(p => _dependencies.Dependencies[i].Url == p.DependencyInfo.Url);
					_dependencies.Dependencies.RemoveAt(i);
					Repository.Remove(url, repoPath);
				}
			}

			//TODO: if we could referesh a folder only that would be nice. cant reimport assets that dont exists.
			AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);

			UpdateDependencies(_dependencies.Dependencies);
		}

		private void OnFocus()
		{
			UpdateDependencies();
		}

		private void OnEnable()
		{
			UpdateDependencies();

			_showAddDependencyMenu = new AnimBool(false);
			_showAddDependencyMenu.valueChanged.AddListener(Repaint);

			_tester = new RepositoryTester();
			EditorApplication.update += UpdateEditor;
		}

		private void OnDisable()
		{
			EditorApplication.update -= UpdateEditor;
		}

		private void UpdateEditor()
		{
			bool repaint = false;

			if (_tester.Testing)
			{
				_tester.Update();
				repaint = true;
			}

			//All just to get a damn warning message updating properly.
			double timeSinceButtonPress = EditorApplication.timeSinceStartup - _addTime;
			if (timeSinceButtonPress < _minimumAddTimeBeforeFeedback)
			{
				lastFrameWasWaitingToShowWarning = true;
			}
			else if (lastFrameWasWaitingToShowWarning)
			{
				repaint = true;
				lastFrameWasWaitingToShowWarning = false;
			}

			_reposBusy.Clear();
			foreach (RepoPanel panel in _repoPanels)
			{
				if (panel.Update())
				{
					repaint = true;
				}

				if (panel.Busy())
				{
					_reposBusy.Add(panel);
				}
			}

			List<RepoPanel> updatedRepos = new List<RepoPanel>();

			//Repos just finished updating. Time to copy.
			foreach (RepoPanel panel in _repoPanels)
			{
				//check what repos just finished updating
				if (_reposWereBusy.Contains(panel) && !_reposBusy.Contains(panel))
				{
					updatedRepos.Add(panel);
					//coppiedAssets.AddRange(panel.CopyRepository());
				}
			}

		//	if(coppiedAssets.Count > 0)
			//{
			//	UpdateAssetDatabaseForNewAssets(coppiedAssets, EditorPrefs.GetBool(FULL_RE_IMPORT_KEY, true), updatedRepos.ToArray());
			//}

			if (repaint)
			{
				Repaint();
			}

			_reposWereBusy = new HashSet<RepoPanel>(_reposBusy);
		}

		private void OnGUI()
		{
			Rect labelRect = EditorGUILayout.GetControlRect();
			labelRect.y += labelRect.height / 2f;

			Rect updateAllRect = labelRect;
			updateAllRect.width = 70;
			updateAllRect.height = 15;
			updateAllRect.x = labelRect.width - 70;

			Rect openCacheRect = labelRect;
			openCacheRect.width = 70;
			openCacheRect.height = 15;
			openCacheRect.x = labelRect.width - (70 * 2);

			Rect cancelAllRect = labelRect;
			cancelAllRect.width = 70;
			cancelAllRect.height = 15;
			cancelAllRect.x = labelRect.width - (70 * 2);

			GUI.Label(labelRect, "Repositories" /*(" + Repository.TotalInitialized + ")"*/, EditorStyles.miniLabel);

			if (GUI.Button(updateAllRect, new GUIContent("Update All", "Update all repositories. You will be asked before overwriting local changes."), EditorStyles.toolbarButton))
			{
				bool localChangesDetected = false;
				for (int i = 0; i < _repoPanels.Count; i++)
				{
					if(_repoPanels[i].HasLocalChanges())
					{
						localChangesDetected = true;
						break;
					}
				}

				if(localChangesDetected)
				{
					int choice = EditorUtility.DisplayDialogComplex("Local Changes Detected", "One or more repositories have local changes. Proceed with caution.", "Update and wipe all changes",  "Cancel", "Update only clean repositories");
					switch(choice)
					{
						case 0:
						{
							//Update and wipe all.
							for (int i = 0; i < _repoPanels.Count; i++)
							{
								_repoPanels[i].UpdateRepository();
							}
							break;
						}
						case 1:
						{
							//Do nothing on cancel.
							break;
						}
						case 2:
						{
							//Update only clean
							for (int i = 0; i < _repoPanels.Count; i++)
							{
								if (!_repoPanels[i].HasLocalChanges())
								{
									_repoPanels[i].UpdateRepository();
								}
							}
							break;
						}
					}
				}
				else
				{
					for (int i = 0; i < _repoPanels.Count; i++)
					{
						_repoPanels[i].UpdateRepository();
					}
				}
			}

			if (GUI.Button(cancelAllRect, new GUIContent("Open Cache", "Open the cache folder root. All repositories for all projects are stored here."), EditorStyles.toolbarButton))
			{
				Process.Start(new ProcessStartInfo()
				{
					FileName = RepoPanel.CacheRoot,
					UseShellExecute = true,
					Verb = "open"
				});
			}

			if (_reposBusy.Count > 0)
			{
				//TODO: Cancel not fully implemented due to complexities in libgit2sharp. Hiding for now.
				/*if (GUI.Button(cancelAllRect, "Cancel All", EditorStyles.miniButton))
				{
					for (int i = 0; i < _repoPanels.Count; i++)
					{
						_repoPanels[i].CancelUpdateRepository();
					}
				}*/
			}

			GUIUtility.DrawLine();

			for (int i = 0; i < _repoPanels.Count; i++)
			{
				_repoPanels[i].OnDrawGUI(i);
			}

			GUIUtility.DrawLine();

			//This is a dumb but kidna fancy way of adding new dependencies.
			if (_tester.Testing)
			{
				GUI.enabled = false;
			}

			if (EditorGUILayout.BeginFadeGroup(_showAddDependencyMenu.faded))
			{

				_potentialNewDependency.Url = EditorGUILayout.TextField("Url", _potentialNewDependency.Url);

				//TODO: For now tags are not exposed. When we do expose them we may have to have both branch and tag as git expects a branch in lots of places
				//Unless we can get branch from tag but not sure its worth the effort
				_potentialNewDependency.Branch = EditorGUILayout.TextField("Branch", _potentialNewDependency.Branch);
				//_potentialNewDependency.Tag = null;

				EditorGUILayout.Space();

				_potentialNewDependency.Name = EditorGUILayout.TextField("Name", _potentialNewDependency.Name);
				_potentialNewDependency.SubFolder = EditorGUILayout.TextField("Subfolder", _potentialNewDependency.SubFolder);

				/*GUILayout.BeginHorizontal();
				selectedFilterIndex = EditorGUILayout.Popup(selectedFilterIndex, new string[] { "Branch", "Tag" }, GUILayout.Width(146));

				if (selectedFilterIndex == 0)
				{
					if (_potentialNewDependency.Branch == null) _potentialNewDependency.Branch = "master";
					_potentialNewDependency.Branch = GUILayout.TextField(_potentialNewDependency.Branch);
					_potentialNewDependency.Tag = null;
				}
				else
				{
					if (_potentialNewDependency.Tag == null) _potentialNewDependency.Tag = "";
					_potentialNewDependency.Tag = GUILayout.TextField(_potentialNewDependency.Tag);
					_potentialNewDependency.Branch = null;
				}
				GUILayout.EndHorizontal();*/
			}
			else
			{
				addDependencyFailureMessage = string.Empty;
			}

			EditorGUILayout.EndFadeGroup();

			Rect addButtonRect = EditorGUILayout.GetControlRect();
			Rect cancelButtonRect = new Rect(addButtonRect);
			if (_showAddDependencyMenu.target)
			{
				addButtonRect = new Rect(addButtonRect.x, addButtonRect.y, addButtonRect.width * 0.6666f, addButtonRect.height);
				cancelButtonRect = new Rect(addButtonRect.x + addButtonRect.width, cancelButtonRect.y, cancelButtonRect.width * 0.33333f, cancelButtonRect.height);
			}

			if (!_tester.Testing)
			{
				if (GUI.Button(addButtonRect, _showAddDependencyMenu.target ? "Add" : "Add Repository", EditorStyles.miniButton))
				{
					addDependencyFailureMessage = string.Empty;
					_addTime = EditorApplication.timeSinceStartup;

					if (!_showAddDependencyMenu.target)
					{
						_showAddDependencyMenu.target = true;
					}
					else
					{
						//simple validation of fields
						bool validationSuccess = true;

						if (String.IsNullOrEmpty(_potentialNewDependency.Branch)/* && String.IsNullOrEmpty(_potentialNewDependency.Tag)*/)
						{
							addDependencyFailureMessage = "Either a valid branch or tag must be specified";
							validationSuccess = false;
						}

						foreach (Dependency dep in _dependencies.Dependencies)
						{
							if (dep.Name.Trim().ToLower() == _potentialNewDependency.Name.Trim().ToLower())
							{
								addDependencyFailureMessage = "Name already exists.";
								validationSuccess = false;
							}
							else if (dep.Url.Trim().ToLower() == _potentialNewDependency.Url.Trim().ToLower())
							{
								addDependencyFailureMessage = "Repository already exists with the current url.\nExisting: " + dep.Name;
								validationSuccess = false;
							}
						}

						if (String.IsNullOrEmpty(_potentialNewDependency.Name))
						{
							addDependencyFailureMessage = "Name can not be empty.";
							validationSuccess = false;
						}

						if (validationSuccess)
						{
							//actually connect to repository
							_tester.Test(_potentialNewDependency.Url, _potentialNewDependency.Branch, _potentialNewDependency.SubFolder, (success, message) =>
							{
								if (success)
								{
									_dependencies.Dependencies.Add(_potentialNewDependency);
									UpdateDependencies(_dependencies.Dependencies);

									//Update the newly added repo
									foreach (RepoPanel panel in _repoPanels)
									{
										if (panel.DependencyInfo.Url == _potentialNewDependency.Url &&
											panel.DependencyInfo.Branch == _potentialNewDependency.Branch &&
											panel.DependencyInfo.Name == _potentialNewDependency.Name)
										{
											//force this as we want to copy
											panel.UpdateRepository();
										}
									}

									CloseAddMenu();
								}
								else
								{
									addDependencyFailureMessage = message;
								}
							});
						}
					}
				}
			}
			else
			{
				GUI.Label(addButtonRect, "Testing connection" + GUIUtility.GetLoadingDots() + "\n" + _potentialNewDependency.Url, EditorStyles.boldLabel);
			}

			GUI.enabled = true;

			if (_showAddDependencyMenu.target)
			{
				if (GUI.Button(cancelButtonRect, "Cancel", EditorStyles.miniButton))
				{
					CloseAddMenu();
				}
			}

			if (Event.current.type == EventType.Layout)
			{
				// Give the failure message a slight delay so we can see that the message is new even if its the same.
				double timeSinceButtonPress = EditorApplication.timeSinceStartup - _addTime;
				showWarningMessage = (!string.IsNullOrEmpty(addDependencyFailureMessage) && timeSinceButtonPress > _minimumAddTimeBeforeFeedback);
			}

			if (showWarningMessage)
			{
				EditorGUILayout.HelpBox(addDependencyFailureMessage, MessageType.Warning);
			}
		}

		private void CloseAddMenu()
		{
			//selectedFilterIndex = 0;
			addDependencyFailureMessage = string.Empty;
			_showAddDependencyMenu.target = false;
			_potentialNewDependency = new Dependency();
		}
	}
}