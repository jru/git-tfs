using System.Collections.Generic;

namespace Sep.Git.Tfs.Core
{
    public interface ITfsChangeset
    {
        TfsChangesetInfo Summary { get; }
        GitCommitBuilder Apply(IGitRepository repository, string lastCommit, ITfsWorkspace workspace);
        GitCommitBuilder CopyTree(IGitRepository repo, ITfsWorkspace workspace);

        /// <summary>
        /// Get all items (files and folders) in the source TFS repository.
        /// </summary>
        IEnumerable<TfsTreeEntry> GetFullTree();

        /// <summary>
        /// Get all files that git-tfs should copy from the source TFS repository. (skips folders and ignored files)
        /// </summary>
        IEnumerable<TfsTreeEntry> GetTree();
    }
}
