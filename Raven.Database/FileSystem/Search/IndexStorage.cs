﻿using Lucene.Net.Documents;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Store;
using Raven.Abstractions.Data;
using Raven.Abstractions.FileSystem;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Database.Config;
using Raven.Database.Extensions;
using Raven.Database.FileSystem.Storage;
using Raven.Database.FileSystem.Util;
using Raven.Database.Impl;
using Raven.Database.Indexing;
using Raven.Json.Linq;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.ConstrainedExecution;

namespace Raven.Database.FileSystem.Search
{
    /// <summary>
    /// 	Thread safe, single instance for the entire application
    /// </summary>
    public class IndexStorage : CriticalFinalizerObject, IDisposable
	{
        private const string IndexVersion = "1.0.0.0";

        private static readonly ILog log = LogManager.GetCurrentClassLogger();
        private static readonly ILog startupLog = LogManager.GetLogger(typeof(IndexStorage).FullName + ".Startup");

		private const string DateIndexFormat = "yyyy-MM-dd_HH-mm-ss";
		private static readonly string[] NumericIndexFields = new[] { "__size_numeric" };

        private readonly string name;
        private readonly InMemoryRavenConfiguration configuration; 
        private readonly object writerLock = new object();
        private readonly IndexSearcherHolder currentIndexSearcherHolder = new IndexSearcherHolder();

		private string indexDirectory;
		private FSDirectory directory;
        private RavenFileSystem filesystem;
        
		private LowerCaseKeywordAnalyzer analyzer;		
        private SnapshotDeletionPolicy snapshotter;
        private IndexWriter writer;
		
        private FileStream crashMarker;
        private bool resetIndexOnUncleanShutdown = false;

        public IndexStorage(string name, InMemoryRavenConfiguration configuration)
		{
            if (configuration == null)
                throw new ArgumentNullException("configuration");
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("name");

            this.name = name;
            this.configuration = configuration;   
		}


        public void Initialize(RavenFileSystem filesystem)
        {
            try
            {
                this.filesystem = filesystem;                                
                this.indexDirectory = configuration.FileSystem.IndexStoragePath;                

                if (System.IO.Directory.Exists(indexDirectory) == false)
                    System.IO.Directory.CreateDirectory(indexDirectory);

                var filesystemDirectory = this.configuration.FileSystem.DataDirectory;
                var crashMarkerPath = Path.Combine(filesystemDirectory, "indexing.crash-marker");

                if (File.Exists(crashMarkerPath))
                {
                    // the only way this can happen is if we crashed because of a power outage
                    // in this case, we consider open indexes to be corrupt and force them
                    // to be reset. This is because to get better perf, we don't flush the files to disk,
                    // so in the case of a power outage, we can't be sure that there wasn't still stuff in
                    // the OS buffer that wasn't written yet.

                    resetIndexOnUncleanShutdown = true;
                }

                // The delete on close ensures that the only way this file will exists is if there was
                // a power outage while the server was running.
                crashMarker = File.Create(crashMarkerPath, 16, FileOptions.DeleteOnClose);
                                
                OpenIndexOnStartup();
            }
            catch
            {
                Dispose();
                throw;
            }           
        }

        private void OpenIndexOnStartup()
        {            
            this.analyzer = new LowerCaseKeywordAnalyzer();
            this.snapshotter = new SnapshotDeletionPolicy(new KeepOnlyLastCommitDeletionPolicy());          

            bool resetTried = false;
            bool recoveryTried = false;
            while (true)
            {
                FSDirectory luceneDirectory = null;
                try
                {
                    luceneDirectory = OpenOrCreateLuceneDirectory(this.indexDirectory);

                    if (!IsIndexStateValid(luceneDirectory))
                        throw new InvalidOperationException("Sanity check on the index failed.");

                    this.directory = luceneDirectory;
                    this.writer = new IndexWriter(directory, analyzer, snapshotter, IndexWriter.MaxFieldLength.UNLIMITED);
                    this.writer.SetMergeScheduler(new ErrorLoggingConcurrentMergeScheduler());

                    this.currentIndexSearcherHolder.SetIndexSearcher(new IndexSearcher(this.directory, true));

                    break;
                }
                catch (Exception e)
                {
                    if (resetTried)
                        throw new InvalidOperationException("Could not open / create index for file system '" + this.name + "', reset already tried", e);

                    if (recoveryTried == false && luceneDirectory != null)
                    {
                        recoveryTried = true;
                        startupLog.WarnException("Could not open index for file system '" + this.name + "'. Trying to recover index", e);
                        startupLog.Info("Recover functionality is still not implemented. Skipping.");
                    }
                    else
                    {
                        resetTried = true;
                        startupLog.WarnException("Could not open index for file system '" + this.name + "'. Recovery operation failed, forcibly resetting index", e);

                        TryResettingIndex();                        
                    }
                }
            }          
        }

        private bool IsIndexStateValid(FSDirectory luceneDirectory)
        {
            // 1. If commitData is null it means that there were no commits, so just in case we are resetting to Etag.Empty
            // 2. If no 'LastEtag' in commitData then we consider it an invalid index
            // 3. If 'LastEtag' is present (and valid), then resetting to it (if it is lower than lastStoredEtag)

            var commitData = IndexReader.GetCommitUserData(luceneDirectory);            

            string value;
            Etag lastEtag = null;
            if (commitData != null && commitData.TryGetValue("LastEtag", out value))
                Etag.TryParse(value, out lastEtag); // etag will be null if parsing will fail

            var lastStoredEtag = GetLastEtagForIndex() ?? Etag.Empty;
            lastEtag = lastEtag ?? Etag.Empty;

            if (EtagUtil.IsGreaterThan(lastEtag, lastStoredEtag))
                return false;

            return true;
        }

        protected Etag GetLastEtagForIndex()
        {
            Etag etag = null;
            filesystem.Storage.Batch(accessor => etag = accessor.GetLastEtag());
            return etag != null ? etag : Etag.Empty;
        }

        internal void TryResettingIndex()
        {
            try
            {
                IOExtensions.DeleteDirectory(this.indexDirectory);

                var luceneDirectory = OpenOrCreateLuceneDirectory(this.indexDirectory);

                using ( var writer = new IndexWriter(luceneDirectory, analyzer, snapshotter, IndexWriter.MaxFieldLength.UNLIMITED) )
                {
                    writer.SetMergeScheduler(new ErrorLoggingConcurrentMergeScheduler());

                    this.filesystem.Storage.Batch(accessor =>
                    {
                        foreach (var file in accessor.GetFilesAfter(Etag.Empty, int.MaxValue))
                            this.Index(writer, FileHeader.Canonize(file.FullPath), file.Metadata);
                    });

                    writer.Flush(true, true, true);
                }
			}
			catch (Exception exception)
			{
				throw new InvalidOperationException("Could not reset index for file system: " + this.name, exception);
			}
        }

        private Lucene.Net.Store.FSDirectory OpenOrCreateLuceneDirectory(string path)
        {
            var luceneDirectory = FSDirectory.Open(new DirectoryInfo(path));

            try
            {
                // We check if the directory already exists
                if (!IndexReader.IndexExists(luceneDirectory))
                {
                    WriteIndexVersion(luceneDirectory);

                    //creating index structure if we need to
                    new IndexWriter(luceneDirectory, analyzer, snapshotter, IndexWriter.MaxFieldLength.UNLIMITED).Dispose();
                }
                else
                {
                    // We prepare in case we have to change the index definition to have a proper upgrade path.
                    EnsureIndexVersionMatches(luceneDirectory);

                    if (luceneDirectory.FileExists("write.lock")) // force lock release, because it was still open when we shut down
                    {
                        IndexWriter.Unlock(luceneDirectory);

                        // for some reason, just calling unlock doesn't remove this file
                        luceneDirectory.DeleteFile("write.lock");
                    }

                    if (luceneDirectory.FileExists("writing-to-index.lock")) // we had an unclean shutdown
                    {
                        if (resetIndexOnUncleanShutdown)
                            throw new InvalidOperationException(string.Format("Rude shutdown detected on '{0}' index in '{1}' directory.", this.name, path));

                        CheckIndexAndTryToFix(luceneDirectory);
                        luceneDirectory.DeleteFile("writing-to-index.lock");
                    }
                }

                return luceneDirectory;
            }
            catch
            {
                luceneDirectory.Dispose();
                throw;
            }
        }

        private const string indexVersionFilename = "index.version";

        private void EnsureIndexVersionMatches(FSDirectory directory)
        {
            if (directory.FileExists(indexVersionFilename) == false)
                throw new InvalidOperationException("Could not find " + indexVersionFilename + " for '" + this.name + "', resetting index");
            
            using (var indexInput = directory.OpenInput(indexVersionFilename))
            {
                var versionToCheck = IndexVersion;
                var versionFromDisk = indexInput.ReadString();
                if (versionFromDisk != versionToCheck)
                    throw new InvalidOperationException("Index for " + this.name + " is of version " + versionFromDisk +
                                                        " which is not compatible with " + versionToCheck + ", resetting index");
            }
        }

        private void WriteIndexVersion(FSDirectory directory)
        {
            using (var indexOutput = directory.CreateOutput(indexVersionFilename))
            {
                indexOutput.WriteString(IndexVersion);
                indexOutput.Flush();
            }
        }

        private void CheckIndexAndTryToFix(FSDirectory directory)
        {
            startupLog.Warn(string.Format("Unclean shutdown detected on file system '{0}', checking the index for errors. This may take a while.", this.name));

            var memoryStream = new MemoryStream();
            var stringWriter = new StreamWriter(memoryStream);
            var checkIndex = new CheckIndex(directory);

            if (startupLog.IsWarnEnabled)
                checkIndex.SetInfoStream(stringWriter);

            var sp = Stopwatch.StartNew();
            var status = checkIndex.CheckIndex_Renamed_Method();
            sp.Stop();

            if (startupLog.IsWarnEnabled)
            {
                startupLog.Warn("Checking index for file system '{0}' took: {1}, clean: {2}", this.name, sp.Elapsed, status.clean);
                memoryStream.Position = 0;

                log.Warn(new StreamReader(memoryStream).ReadToEnd());
            }

            if (status.clean)
                return;

            startupLog.Warn("Attempting to fix index of file system: '{0}'", this.name);
            sp.Restart();
            checkIndex.FixIndex(status);
            startupLog.Warn("Fixed index of file system '{0}' in {1}", this.name, sp.Elapsed);
        }

		public string[] Query(string query, string[] sortFields, int start, int pageSize, out int totalResults)
		{
			IndexSearcher searcher;
			using (GetSearcher(out searcher))
			{
				Query q;
				if (string.IsNullOrEmpty(query))
				{
					q = new MatchAllDocsQuery();
				}
				else
				{
					var queryParser = new RavenQueryParser(analyzer, NumericIndexFields);
                    q = queryParser.Parse(query);
				}

				var topDocs = ExecuteQuery(searcher, sortFields, q, pageSize + start);

				var results = new List<string>();

				for (var i = start; i < pageSize + start && i < topDocs.TotalHits; i++)
				{
					var document = searcher.Doc(topDocs.ScoreDocs[i].Doc);
					results.Add(document.Get("__key"));
				}
				totalResults = topDocs.TotalHits;
				return results.ToArray();
			}
		}

		private TopDocs ExecuteQuery(IndexSearcher searcher, string[] sortFields, Query q, int size)
		{
			TopDocs topDocs;
			if (sortFields != null && sortFields.Length > 0)
			{
				var sort = new Sort(sortFields.Select(field =>
				{
					var desc = field.StartsWith("-");
					if (desc)
						field = field.Substring(1);
					return new SortField(field, SortField.STRING, desc);
				}).ToArray());
				topDocs = searcher.Search(q, null, size, sort);
			}
			else
			{
				topDocs = searcher.Search(q, null, size);
			}
			return topDocs;
		}

        private void Index(IndexWriter writer, string key, RavenJObject metadata)
        {
            lock (writerLock)
            {
                var lowerKey = key.ToLowerInvariant();

                var doc = CreateDocument(lowerKey, metadata);

                // REVIEW: Check if there is more straight-forward/efficient pattern out there to work with RavenJObjects.
                var lookup = metadata.ToLookup(x => x.Key);
                foreach (var metadataKey in lookup)
                {
                    foreach (var metadataHolder in metadataKey)
                    {
                        var array = metadataHolder.Value as RavenJArray;
                        if (array != null)
                        {
                            // Object is an array. Therefore, we index each token. 
                            foreach (var item in array)
                                doc.Add(new Field(metadataHolder.Key, item.ToString(), Field.Store.NO, Field.Index.ANALYZED_NO_NORMS));
                        }
                        else
                        {
                            doc.Add(new Field(metadataHolder.Key, metadataHolder.Value.ToString(), Field.Store.NO, Field.Index.ANALYZED_NO_NORMS));
                        }                            
                    }
                }

                writer.DeleteDocuments(new Term("__key", lowerKey));
                writer.AddDocument(doc);

                // yes, this is slow, but we aren't expecting high writes count
                var etag = lookup["ETag"].First();
                var customCommitData = new Dictionary<string, string>() { { "LastETag", etag.Value.ToString() } };
                writer.Commit(customCommitData);
                ReplaceSearcher(writer);
            }
        }

        public virtual void Index(string key, RavenJObject metadata)
        {
            Index(this.writer, key, metadata);
        }

        private static Document CreateDocument(string lowerKey, RavenJObject metadata)
        {
            var doc = new Document();
            doc.Add(new Field("__key", lowerKey, Field.Store.YES, Field.Index.NOT_ANALYZED_NO_NORMS));

            var fileName = Path.GetFileName(lowerKey);            
            Debug.Assert(fileName != null);
            doc.Add(new Field("__fileName", fileName, Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
            // the reversed version of the file name is used to allow searches that start with wildcards
            char[] revFileName = fileName.ToCharArray();
            Array.Reverse(revFileName);
            doc.Add(new Field("__rfileName", new string(revFileName), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));                        

            int level = 0;
            var directoryName = RavenFileNameHelper.RavenDirectory(Path.GetDirectoryName(lowerKey));


            doc.Add(new Field("__directory", directoryName, Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
            // the reversed version of the directory is used to allow searches that start with wildcards
            char[] revDirectory = directoryName.ToCharArray();
            Array.Reverse(revDirectory);
            doc.Add(new Field("__rdirectory", new string(revDirectory), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));

            do
            {
                level += 1;

                directoryName = RavenFileNameHelper.RavenDirectory(directoryName);

                doc.Add(new Field("__directoryName", directoryName, Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
                // the reversed version of the directory is used to allow searches that start with wildcards
                char[] revDirectoryName = directoryName.ToCharArray();
                Array.Reverse(revDirectoryName);
                doc.Add(new Field("__rdirectoryName", new string(revDirectoryName), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));

                directoryName = Path.GetDirectoryName(directoryName);
            } 
            while (directoryName != null);

            doc.Add(new Field("__modified", DateTime.UtcNow.ToString(DateIndexFormat, CultureInfo.InvariantCulture), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
            doc.Add(new Field("__level", level.ToString(CultureInfo.InvariantCulture), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
            
            RavenJToken contentLen;
            if ( metadata.TryGetValue("Content-Length", out contentLen))
            {
                long len;
                if (long.TryParse(contentLen.Value<string>(), out len))
                {
                    doc.Add(new Field("__size", len.ToString("D20"), Field.Store.NO, Field.Index.NOT_ANALYZED_NO_NORMS));
                    doc.Add(new NumericField("__size_numeric", Field.Store.NO, true).SetLongValue(len));
                }
            }

            return doc;
        }

        internal IDisposable GetSearcher(out IndexSearcher searcher)
		{
			return currentIndexSearcherHolder.GetSearcher(out searcher);
		}


        private void SafeDispose ( IDisposable disposable )
        {
            if (disposable != null)
                disposable.Dispose();
        }

		public void Dispose()
		{
            var exceptionAggregator = new ExceptionAggregator(log, string.Format("Could not properly close index storage for file system '{0}'", this.name));

            exceptionAggregator.Execute(() => { if (analyzer != null) analyzer.Close(); });
            exceptionAggregator.Execute(() => { if (currentIndexSearcherHolder != null)  currentIndexSearcherHolder.SetIndexSearcher(null); });
            exceptionAggregator.Execute(() => SafeDispose(crashMarker) );
            exceptionAggregator.Execute(() => SafeDispose(writer) );
            exceptionAggregator.Execute(() => SafeDispose(directory) );

            exceptionAggregator.ThrowIfNeeded();
		}

		public void Delete(string key)
		{
			var lowerKey = key.ToLowerInvariant();

			lock (writerLock)
			{
				writer.DeleteDocuments(new Term("__key", lowerKey));
				writer.Optimize();
				writer.Commit();
                ReplaceSearcher(writer);
			}
		}

        private void ReplaceSearcher(IndexWriter writer)
		{
			currentIndexSearcherHolder.SetIndexSearcher(new IndexSearcher(writer.GetReader()));
		}

		public IEnumerable<string> GetTermsFor(string field, string fromValue)
		{
			IndexSearcher searcher;
			using (GetSearcher(out searcher))
			{
				var termEnum = searcher.IndexReader.Terms(new Term(field, fromValue ?? string.Empty));
				try
				{
					if (string.IsNullOrEmpty(fromValue) == false) // need to skip this value
					{
						while (termEnum.Term == null || fromValue.Equals(termEnum.Term.Text))
						{
							if (termEnum.Next() == false)
								yield break;
						}
					}
					while (termEnum.Term == null ||
						field.Equals(termEnum.Term.Field))
					{
						if (termEnum.Term != null)
						{
							var item = termEnum.Term.Text;
							yield return item;
						}

						if (termEnum.Next() == false)
							break;
					}
				}
				finally
				{
					termEnum.Dispose();
				}
			}
		}

        public void Backup(string backupDirectory)
	    {
            bool hasSnapshot = false;
            bool throwOnFinallyException = true;
            try
            {
                var existingFiles = new HashSet<string>();
                var allFilesPath = Path.Combine(backupDirectory, "index-files.all-existing-index-files");
                var saveToFolder = Path.Combine(backupDirectory, "Indexes");
                System.IO.Directory.CreateDirectory(saveToFolder);
                if (File.Exists(allFilesPath))
                {
                    foreach (var file in File.ReadLines(allFilesPath))
                    {
                        existingFiles.Add(file);
                    }
                }

                var neededFilePath = Path.Combine(saveToFolder, "index-files.required-for-index-restore");
                using (var allFilesWriter = File.Exists(allFilesPath) ? File.AppendText(allFilesPath) : File.CreateText(allFilesPath))
                using (var neededFilesWriter = File.CreateText(neededFilePath))
                {
                    var segmentsFileName = "segments.gen";
                    var segmentsFullPath = Path.Combine(indexDirectory, segmentsFileName);
                    File.Copy(segmentsFullPath, Path.Combine(saveToFolder, segmentsFileName));

                    allFilesWriter.WriteLine(segmentsFileName);
                    neededFilesWriter.WriteLine(segmentsFileName);

                    var versionFileName = "index.version";
                    var versionFullPath = Path.Combine(indexDirectory, versionFileName);
                    File.Copy(versionFullPath, Path.Combine(saveToFolder, versionFileName));

                    allFilesWriter.WriteLine(versionFileName);
                    neededFilesWriter.WriteLine(versionFileName);                    

                    var commit = snapshotter.Snapshot();
                    hasSnapshot = true;
                    foreach (var fileName in commit.FileNames)
                    {
                        var fullPath = Path.Combine(indexDirectory, fileName);

                        if (".lock".Equals(Path.GetExtension(fullPath), StringComparison.InvariantCultureIgnoreCase))
                            continue;

                        if (File.Exists(fullPath) == false)
                            continue;

                        if (existingFiles.Contains(fileName) == false)
                        {
                            var destFileName = Path.Combine(saveToFolder, fileName);
                            try
                            {
                                File.Copy(fullPath, destFileName);
                            }
                            catch (Exception e)
                            {
                                log.WarnException(
                                    "Could not backup RavenFS index" + 
                                    " because failed to copy file : " + fullPath + ". Skipping the index, will force index reset on restore", e); //TODO: is it also true for RavenFS?
                                neededFilesWriter.Dispose();
                                TryDelete(neededFilePath);
                                return;

                            }
                            allFilesWriter.WriteLine(fileName);
                        }
                        neededFilesWriter.WriteLine(fileName);
                    }
                    allFilesWriter.Flush();
                    neededFilesWriter.Flush();
                }
            }
            catch
            {
                throwOnFinallyException = false;
                throw;
            }
            finally
            {
                if (snapshotter != null && hasSnapshot)
                {
                    try
                    {
                        snapshotter.Release();
                    }
                    catch
                    {
                        if (throwOnFinallyException)
                            throw;
                    }
                }
            }
	    }

        private static void TryDelete(string neededFilePath)
        {
            try
            {
                File.Delete(neededFilePath);
            }
            catch (Exception)
            {
            }
        }

    }
}