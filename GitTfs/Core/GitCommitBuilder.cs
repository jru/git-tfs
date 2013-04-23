using System;
using System.Diagnostics;
using System.IO;
using LibGit2Sharp;

namespace Sep.Git.Tfs.Core
{
    public class GitCommitBuilder
    {
        public GitCommitBuilder(IGitRepository repository, string lastCommit, LogEntry logEntry)
        {
            this.repository = repository;
            if (!string.IsNullOrWhiteSpace(lastCommit))
                this.treeDef = TreeDefinition.From(repository.GetCommit(lastCommit).Tree);
            this.logEntry = logEntry;
        }

        public LogEntry LogEntry
        {
            get { return this.logEntry; }
        }

        public string Tree
        {
            get
            {
                return repository.CreateTree(this.treeDef);
            }
        }

        private readonly IGitRepository repository;
        private readonly LogEntry logEntry;

        private TreeDefinition treeDef = new TreeDefinition();
        private int nr = 0;

        public int Remove(string path)
        {
            treeDef.Remove(path);
            Trace.WriteLine("   D " + path);
            return ++nr;
        }

        public int Update(string mode, string path, string localFile)
        {
            Trace.WriteLine("   U " + path);
            treeDef.Add(path, localFile, Mode.ToFileMode(mode));
            return ++nr;
        }
    }
}
