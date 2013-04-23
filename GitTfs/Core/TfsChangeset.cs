using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Sep.Git.Tfs.Core.TfsInterop;
using Sep.Git.Tfs.Util;

namespace Sep.Git.Tfs.Core
{
    public class TfsChangeset : ITfsChangeset
    {
        private readonly ITfsHelper _tfs;
        private readonly IChangeset _changeset;
        private readonly TextWriter _stdout;
        private readonly AuthorsFile _authors;
        public TfsChangesetInfo Summary { get; set; }

        public TfsChangeset(ITfsHelper tfs, IChangeset changeset, TextWriter stdout, AuthorsFile authors)
        {
            _tfs = tfs;
            _changeset = changeset;
            _stdout = stdout;
            _authors = authors;
        }

        public GitCommitBuilder Apply(IGitRepository repo, string lastCommit, ITfsWorkspace workspace)
        {
            GitCommitBuilder commit = new GitCommitBuilder(repo, lastCommit, MakeNewLogEntry());

            var initialTree = repo.GetObjects(lastCommit);
            workspace.Get(_changeset);
            foreach (var change in Sort(_changeset.Changes))
            {
                Apply(change, commit, workspace, initialTree);
            }
            return commit;
        }

        private void Apply(IChange change, GitCommitBuilder commit, ITfsWorkspace workspace, IDictionary<string, GitObject> initialTree)
        {
            // If you make updates to a dir in TF, the changeset includes changes for all the children also,
            // and git doesn't really care if you add or delete empty dirs.
            if (change.Item.ItemType == TfsItemType.File)
            {
                var pathInGitRepo = GetPathInGitRepo(change.Item.ServerItem, initialTree);
                if (pathInGitRepo == null || Summary.Remote.ShouldSkip(pathInGitRepo))
                    return;
                if (change.ChangeType.IncludesOneOf(TfsChangeType.Rename))
                {
                    Rename(change, pathInGitRepo, commit, workspace, initialTree);
                }
                else if (change.ChangeType.IncludesOneOf(TfsChangeType.Delete))
                {
                    Delete(pathInGitRepo, commit, initialTree);
                }
                else
                {
                    Update(change, pathInGitRepo, commit, workspace, initialTree);
                }
            }
        }

        private string GetPathInGitRepo(string tfsPath, IDictionary<string, GitObject> initialTree)
        {
            var pathInGitRepo = Summary.Remote.GetPathInGitRepo(tfsPath);
            if (pathInGitRepo == null)
                return null;
            return UpdateToMatchExtantCasing(pathInGitRepo, initialTree);
        }

        private void Rename(IChange change, string pathInGitRepo, GitCommitBuilder commit, ITfsWorkspace workspace, IDictionary<string, GitObject> initialTree)
        {
            var oldPath = GetPathInGitRepo(GetPathBeforeRename(change.Item), initialTree);
            if (oldPath != null)
            {
                Delete(oldPath, commit, initialTree);
            }
            if (!change.ChangeType.IncludesOneOf(TfsChangeType.Delete))
            {
                Update(change, pathInGitRepo, commit, workspace, initialTree);
            }
        }

        private IEnumerable<IChange> Sort(IEnumerable<IChange> changes)
        {
            return changes.OrderBy(change => Rank(change.ChangeType));
        }

        private int Rank(TfsChangeType type)
        {
            if (type.IncludesOneOf(TfsChangeType.Delete))
                return 0;
            if (type.IncludesOneOf(TfsChangeType.Rename))
                return 1;
            return 2;
        }

        private string GetPathBeforeRename(IItem item)
        {
            var previousChangeset = item.ChangesetId - 1;
            var oldItem = item.VersionControlServer.GetItem(item.ItemId, previousChangeset);
            if (null == oldItem)
            {
                var history = item.VersionControlServer.QueryHistory(item.ServerItem, item.ChangesetId, 0,
                                                                     TfsRecursionType.None, null, 1, previousChangeset,
                                                                     1, true, false, false);
                var previousChange = history.FirstOrDefault();
                if (previousChange == null)
                {
                    Trace.WriteLine(string.Format("No history found for item {0} changesetId {1}", item.ServerItem, item.ChangesetId));
                    return null;
                }
                oldItem = previousChange.Changes[0].Item;
            }
            return oldItem.ServerItem;
        }

        private void Update(IChange change, string pathInGitRepo, GitCommitBuilder index, ITfsWorkspace workspace, IDictionary<string, GitObject> initialTree)
        {
            if (change.Item.DeletionId == 0)
            {
                index.Update(
                    GetMode(change, initialTree, pathInGitRepo),
                    pathInGitRepo,
                    workspace.GetLocalPath(pathInGitRepo)
                );
            }
        }

        public IEnumerable<TfsTreeEntry> GetTree()
        {
            return GetFullTree().Where(item => item.Item.ItemType == TfsItemType.File && !Summary.Remote.ShouldSkip(item.FullName));
        }

        public IEnumerable<TfsTreeEntry> GetFullTree()
        {
            var treeInfo = Summary.Remote.Repository.GetObjects();
            var tfsItems = _changeset.VersionControlServer.GetItems(Summary.Remote.TfsRepositoryPath, _changeset.ChangesetId, TfsRecursionType.Full);
            var tfsItemsWithGitPaths = tfsItems.Select(item => new { item, gitPath = GetPathInGitRepo(item.ServerItem, treeInfo) });
            return tfsItemsWithGitPaths.Where(x => x.gitPath != null).Select(x => new TfsTreeEntry(x.gitPath, x.item));
        }

        public GitCommitBuilder CopyTree(IGitRepository repo, ITfsWorkspace workspace)
        {
            var tfsTreeEntries = GetTree().ToArray();
            if (tfsTreeEntries.Length == 0)
            {
                return new GitCommitBuilder(repo, null, MakeNewLogEntry(_changeset));
            }
            else
            {
                var startTime = DateTime.Now;
                var itemsCopied = 0;
                var maxChangesetId = tfsTreeEntries.Max(e => e.Item.ChangesetId);

                var commit = new GitCommitBuilder(repo, null, MakeNewLogEntry(_tfs.GetChangeset(maxChangesetId)));

                workspace.Get(_changeset.ChangesetId);
                foreach (var entry in tfsTreeEntries)
                {
                    Add(entry.Item, entry.FullName, commit, workspace);

                    itemsCopied++;
                    if (DateTime.Now - startTime > TimeSpan.FromSeconds(30))
                    {
                        _stdout.WriteLine("{0} objects created...", itemsCopied);
                        startTime = DateTime.Now;
                    }
                }
                return commit;
            }
        }

        private void Add(IItem item, string pathInGitRepo, GitCommitBuilder commit)
        {
            if (item.DeletionId == 0)
            {
                // Download the content directly into the git database as a blob:
                using (var temp = item.DownloadFile())
                {
                    commit.Update(Mode.NewFile, pathInGitRepo, temp);
                }
            }
        }

        private void Add(IItem item, string pathInGitRepo, GitCommitBuilder commit, ITfsWorkspace workspace)
        {
            if (item.DeletionId == 0)
            {
                commit.Update(Mode.NewFile, pathInGitRepo, workspace.GetLocalPath(pathInGitRepo));
            }
        }

        private string GetMode(IChange change, IDictionary<string, GitObject> initialTree, string pathInGitRepo)
        {
            if (initialTree.ContainsKey(pathInGitRepo) &&
                !String.IsNullOrEmpty(initialTree[pathInGitRepo].Mode) &&
                !change.ChangeType.IncludesOneOf(TfsChangeType.Add))
            {
                return initialTree[pathInGitRepo].Mode;
            }
            return Mode.NewFile;
        }

        private static readonly Regex SplitDirnameFilename = new Regex("(?<dir>.*)/(?<file>[^/]+)");

        private string UpdateToMatchExtantCasing(string pathInGitRepo, IDictionary<string, GitObject> initialTree)
        {
            if (initialTree.ContainsKey(pathInGitRepo))
                return initialTree[pathInGitRepo].Path;

            var fullPath = pathInGitRepo;
            var splitResult = SplitDirnameFilename.Match(pathInGitRepo);
            if (splitResult.Success)
            {

                var dirName = splitResult.Groups["dir"].Value;
                var fileName = splitResult.Groups["file"].Value;
                fullPath = UpdateToMatchExtantCasing(dirName, initialTree) + "/" + fileName;
            }
            initialTree[fullPath] = new GitObject { Path = fullPath };
            return fullPath;
        }

        private void Delete(string pathInGitRepo, GitCommitBuilder commit, IDictionary<string, GitObject> initialTree)
        {
            if (initialTree.ContainsKey(pathInGitRepo))
            {
                commit.Remove(initialTree[pathInGitRepo].Path);
                Trace.WriteLine("\tD\t" + pathInGitRepo);
            }
        }

        private LogEntry MakeNewLogEntry()
        {
            return MakeNewLogEntry(_changeset);
        }

        private LogEntry MakeNewLogEntry(IChangeset changesetToLog)
        {
            var identity = _tfs.GetIdentity(changesetToLog.Committer);
            var name = changesetToLog.Committer;
            var email = changesetToLog.Committer;
            if (_authors != null && _authors.Authors.ContainsKey(changesetToLog.Committer))
            {
                name = _authors.Authors[changesetToLog.Committer].Name;
                email = _authors.Authors[changesetToLog.Committer].Email;
            }
            else if (identity != null)
            {
                //This can be null if the user was deleted from AD.
                //We want to keep their original history around with as little 
                //hassle to the end user as possible
                if (!String.IsNullOrWhiteSpace(identity.DisplayName))
                    name = identity.DisplayName;

                if (!String.IsNullOrWhiteSpace(identity.MailAddress))
                    email = identity.MailAddress;
            }
            else if (!String.IsNullOrWhiteSpace(changesetToLog.Committer))
            {
                string[] split = changesetToLog.Committer.Split('\\');
                if (split.Length == 2)
                {
                    name = split[1].ToLower();
                    email = string.Format("{0}@{1}.tfs.local", name, split[0].ToLower());
                }
            }

            // committer's & author's name and email MUST NOT be empty as otherwise they would be picked
            // by git from user.name and user.email config settings which is bad thing because commit could
            // be different depending on whose machine it fetched
            if (String.IsNullOrWhiteSpace(name))
            {
                name = "Unknown TFS user";
            }
            if (String.IsNullOrWhiteSpace(email))
            {
                email = "unknown@tfs.local";
            }
            return new LogEntry
                       {
                           Date = changesetToLog.CreationDate,
                           Log = changesetToLog.Comment + Environment.NewLine,
                           ChangesetId = changesetToLog.ChangesetId,
                           CommitterName = name,
                           AuthorName = name,
                           CommitterEmail = email,
                           AuthorEmail = email
                       };
        }
    }
}
