﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Duplicati.Library.Main.Volumes;

namespace Duplicati.Library.Main.Database
{
    internal partial class LocalRestoreDatabase : LocalDatabase
    {
        protected string m_tempfiletable;
        protected string m_tempblocktable;
        protected long m_blocksize;
        protected DateTime m_restoreTime;

		public DateTime RestoreTime { get { return m_restoreTime; } } 

        public LocalRestoreDatabase(string path, long blocksize)
            : this(new LocalDatabase(path, "Restore"), blocksize)
        {
        }

        public LocalRestoreDatabase(LocalDatabase dbparent, long blocksize)
            : base(dbparent)
        {
            //TODO: Should read this from DB?
            m_blocksize = blocksize;
        }

        public void PrepareRestoreFilelist(DateTime restoretime, long[] versions, Library.Utility.IFilter filter, ILogWriter log)
        {
            var guid = Library.Utility.Utility.ByteArrayAsHexString(Guid.NewGuid().ToByteArray());

            m_tempfiletable = "Fileset-" + guid;
            m_tempblocktable = "Blocks-" + guid;

            using(var cmd = m_connection.CreateCommand())
            {
                long filesetId = GetFilesetID(restoretime, versions);
                m_restoreTime = Convert.ToDateTime(cmd.ExecuteScalar(@"SELECT ""Timestamp"" FROM ""Fileset"" WHERE ""ID"" = ?", filesetId));
                cmd.Parameters.Clear();

                cmd.CommandText = string.Format(@"CREATE TEMPORARY TABLE ""{0}"" (""ID"" INTEGER PRIMARY KEY, ""Path"" TEXT NOT NULL, ""BlocksetID"" INTEGER NOT NULL, ""MetadataID"" INTEGER NOT NULL, ""Targetpath"" TEXT NULL ) ", m_tempfiletable);
                cmd.ExecuteNonQuery();

                cmd.CommandText = string.Format(@"CREATE TEMPORARY TABLE ""{0}"" (""ID"" INTEGER PRIMARY KEY, ""FileID"" INTEGER NOT NULL, ""Index"" INTEGER NOT NULL, ""Hash"" TEXT NOT NULL, ""Size"" INTEGER NOT NULL, ""Restored"" BOOLEAN NOT NULL)", m_tempblocktable);
                cmd.ExecuteNonQuery();

                if (filter == null || filter.Empty)
                {
                    // Simple case, restore everything
                    cmd.CommandText = string.Format(@"INSERT INTO ""{0}"" (""Path"", ""BlocksetID"", ""MetadataID"") SELECT ""File"".""Path"", ""File"".""BlocksetID"", ""File"".""MetadataID"" FROM ""File"", ""FilesetEntry"" WHERE ""File"".""ID"" = ""FilesetEntry"".""FileID"" AND ""FilesetEntry"".""FilesetID"" = ? ", m_tempfiletable);
                    cmd.AddParameter(filesetId);
                    cmd.ExecuteNonQuery();
                }
                else if (filter is Library.Utility.FilterExpression && (filter as Library.Utility.FilterExpression).Type == Duplicati.Library.Utility.FilterType.Simple)
                {
                    // If we get a list of filenames, the lookup table is faster
                    using(var tr = m_connection.BeginTransaction())
                    {
                        var p = (filter as Library.Utility.FilterExpression).GetSimpleList();
                        var m_filenamestable = "Filenames-" + guid;
                        cmd.Transaction = tr;
                        cmd.ExecuteNonQuery(string.Format(@"CREATE TEMPORARY TABLE ""{0}"" (""Path"" TEXT NOT NULL) ", m_filenamestable));
                        cmd.CommandText = string.Format(@"INSERT INTO ""{0}"" (""Path"") VALUES (?)", m_filenamestable);
                        cmd.AddParameter();
                        
                        foreach(var s in p)
                        {
                            cmd.SetParameterValue(0, s);
                            cmd.ExecuteNonQuery();
                        }
                        
                        //TODO: Handle case-insensitive filename lookup
                        cmd.CommandText = string.Format(@"INSERT INTO ""{0}"" (""Path"", ""BlocksetID"", ""MetadataID"") SELECT ""File"".""Path"", ""File"".""BlocksetID"", ""File"".""MetadataID"" FROM ""File"", ""FilesetEntry"" WHERE ""File"".""ID"" = ""FilesetEntry"".""FileID"" AND ""FilesetEntry"".""FilesetID"" = ? AND ""Path"" IN (SELECT DISTINCT ""Path"" FROM ""{1}"") ", m_tempfiletable, m_filenamestable);
                        cmd.SetParameterValue(0, filesetId);
                        var c = cmd.ExecuteNonQuery();
                        
                        cmd.Parameters.Clear();
                        
                        if (c != p.Length)
                        {
                            var sb = new StringBuilder();
                            sb.AppendLine();
                            
                            using(var rd = cmd.ExecuteReader(string.Format(@"SELECT ""Path"" FROM ""{0}"" WHERE ""Path"" NOT IN (SELECT ""Path"" FROM ""{1}"")", m_filenamestable, m_tempfiletable)))
                                while (rd.Read())
                                    sb.AppendLine(rd.GetValue(0).ToString());

                            DateTime actualrestoretime = Convert.ToDateTime(cmd.ExecuteScalar(@"SELECT ""Timestamp"" FROM ""Fileset"" WHERE ""ID"" = ?", filesetId));
                            log.AddWarning(string.Format("{0} File(s) were not found in list of files for backup at {1}, will not be restored: {2}", p.Length - c, actualrestoretime.ToLocalTime(), sb), null);
                            cmd.Parameters.Clear();
                        }
                        
                        cmd.ExecuteNonQuery(string.Format(@"DROP TABLE ""{0}"" ", m_filenamestable));
                        
                        using(new Logging.Timer("CommitPrepareFileset"))
                            tr.Commit();
                    }
                }
                else
                {
                    // Restore but filter elements based on the filter expression
                    // If this is too slow, we could add a special handler for wildcard searches too
                    cmd.CommandText = string.Format(@"SELECT ""File"".""Path"", ""File"".""BlocksetID"", ""File"".""MetadataID"" FROM ""File"", ""FilesetEntry"" WHERE ""File"".""ID"" = ""FilesetEntry"".""FileID"" AND ""FilesetID"" = ?");
                    cmd.AddParameter(filesetId);

                    object[] values = new object[3];
                    using(var cmd2 = m_connection.CreateCommand())
                    {
                        cmd2.CommandText = string.Format(@"INSERT INTO ""{0}"" (""Path"", ""BlocksetID"", ""MetadataID"") VALUES (?,?,?)", m_tempfiletable);
                        cmd2.AddParameter();
                        cmd2.AddParameter();
                        cmd2.AddParameter();

                        using(var rd = cmd.ExecuteReader())
                            while (rd.Read())
                            {
                                rd.GetValues(values);
                                if (values[0] != null && filter.Matches(values[0].ToString()))
                                {
                                    cmd2.SetParameterValue(0, values[0]);
                                    cmd2.SetParameterValue(1, values[1]);
                                    cmd2.SetParameterValue(2, values[2]);
                                    cmd2.ExecuteNonQuery();
                                }
                            }
                    }
                }
                
                if (log.VerboseOutput)
                    log.AddVerboseMessage("Restore list contains {0} files ", cmd.ExecuteScalar(string.Format(@"SELECT COUNT(*) FROM ""{0}"" ", m_tempfiletable)));
            }
        }

        public string GetLargestPrefix()
        {
            using (var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = string.Format(@"SELECT ""Path"" FROM ""{0}"" ORDER BY LENGTH(""Path"") DESC LIMIT 1", m_tempfiletable);
                var v0 = cmd.ExecuteScalar();
                string maxpath = "";
                if (v0 != null)
                    maxpath = v0.ToString();

                cmd.CommandText = string.Format(@"SELECT COUNT(*) FROM ""{0}""", m_tempfiletable);
                var filecount = Convert.ToInt64(cmd.ExecuteScalar());
                long foundfiles = -1;

                //TODO: Handle FS case-sensitive?
                cmd.CommandText = string.Format(@"SELECT COUNT(*) FROM ""{0}"" WHERE SUBSTR(""Path"", 1, ?) = ?", m_tempfiletable);
                cmd.AddParameter();
                cmd.AddParameter();

                while (filecount != foundfiles && maxpath.Length > 0)
                {
                    var mp = Library.Utility.Utility.AppendDirSeparator(maxpath);
                    cmd.SetParameterValue(0, mp.Length);
                    cmd.SetParameterValue(1, mp);
                    foundfiles = Convert.ToInt64(cmd.ExecuteScalar());

                    if (filecount != foundfiles)
                    {
                        var oldlen = maxpath.Length;
                        maxpath = System.IO.Path.GetDirectoryName(maxpath);
                        if (maxpath.Length == oldlen)
                            maxpath = "";
                    }
                }

                return maxpath == "" ? "" : Library.Utility.Utility.AppendDirSeparator(maxpath);
            }

        }

        public void SetTargetPaths(string largest_prefix, string destination)
		{
			using(var cmd = m_connection.CreateCommand())
			{
				if (string.IsNullOrEmpty(destination))
				{
					//The string fixing here is meant to provide some non-random
					// defaults when restoring cross OS, e.g. backup on Linux, restore on Windows
					//This is mostly meaningless, and the user really should use --restore-path
				
					if (Library.Utility.Utility.IsClientLinux)
	                	// For Win -> Linux, we remove the colon from the drive letter, and use the drive letter as root folder
						cmd.ExecuteNonQuery(string.Format(@"UPDATE ""{0}"" SET ""Targetpath"" = CASE WHEN SUBSTR(""Path"", 2, 1) == "":"" THEN ""/"" || SUBSTR(""Path"", 1, 1) || SUBSTR(""Path"", 3) ELSE ""Path"" END", m_tempfiletable));
					else
	                	// For Linux -> Win, we use the temporary folder's drive as the root path
						cmd.ExecuteNonQuery(string.Format(@"UPDATE ""{0}"" SET ""Targetpath"" = CASE WHEN SUBSTR(""Path"", 1, 1) == ""/"" THEN ? || SUBSTR(""Path"", 2) ELSE ""Path"" END", m_tempfiletable), Library.Utility.Utility.AppendDirSeparator(System.IO.Path.GetPathRoot(Library.Utility.TempFolder.SystemTempPath)));
	                
				}
				else
				{						
					if (string.IsNullOrEmpty(largest_prefix))
					{
						//Special case, restoring to new folder, but files are from different drives
						// So we use the format <restore path> / <drive letter> / <source path>
						// To avoid generating paths with a colon
						cmd.ExecuteNonQuery(string.Format(@"UPDATE ""{0}"" SET ""Targetpath"" = ? || CASE WHEN SUBSTR(""Path"", 2, 1) == "":"" THEN SUBSTR(""Path"", 1, 1) || SUBSTR(""Path"", 3) ELSE ""Path"" END", m_tempfiletable), destination);
					}
					else
					{
						largest_prefix = Library.Utility.Utility.AppendDirSeparator(largest_prefix);
						cmd.CommandText = string.Format(@"UPDATE ""{0}"" SET ""Targetpath"" = ? || SUBSTR(""Path"", ?)", m_tempfiletable);
						cmd.AddParameter(destination);
						cmd.AddParameter(largest_prefix.Length + 1);
						cmd.ExecuteNonQuery();
					}
               	}
            }
        }

        public void FindMissingBlocks(ILogWriter log)
        {
            using(var cmd = m_connection.CreateCommand())
            {
                cmd.CommandText = string.Format(@"INSERT INTO ""{0}"" (""FileID"", ""Index"", ""Hash"", ""Size"", ""Restored"") SELECT DISTINCT ""{1}"".""ID"", ""BlocksetEntry"".""Index"", ""Block"".""Hash"", ""Block"".""Size"", 0 FROM ""{1}"", ""BlocksetEntry"", ""Block"" WHERE ""{1}"".""BlocksetID"" = ""BlocksetEntry"".""BlocksetID"" AND ""BlocksetEntry"".""BlockID"" = ""Block"".""ID"" ", m_tempblocktable, m_tempfiletable);
                var p = cmd.ExecuteNonQuery();
                
                if (log.VerboseOutput)
                {
                    var size = Convert.ToInt64(cmd.ExecuteScalar(string.Format(@"SELECT SUM(""Size"") FROM ""{0}"" ", m_tempblocktable)));
                    log.AddVerboseMessage("Restore list contains {0} blocks with a total size of {1}", p, Library.Utility.Utility.FormatSizeString(size));
                }
            }
        }

		public void UpdateTargetPath(long ID, string newname)
		{
            using (var cmd = m_connection.CreateCommand())
            	cmd.ExecuteNonQuery(string.Format(@"UPDATE ""{0}"" SET ""TargetPath"" = ? WHERE ""ID"" = ?", m_tempfiletable), newname, ID);
		}

        public interface IExistingFileBlock
        {
            string Hash { get; }
            long Index { get; }
            long Size { get; }
        }

        public interface IExistingFile
        {
            string TargetPath { get; }
            string TargetHash { get; }
            long TargetFileID { get; }
            long Length { get; }
            IEnumerable<IExistingFileBlock> Blocks { get; }
        }


        public interface IBlockSource
        {
            string Path { get; }
            long Size { get; }
            long Offset { get; }
        }

        public interface IBlockDescriptor
        {
            string Hash { get; }
            long Size { get; }
            long Offset { get; }
            long Index { get; }
            IEnumerable<IBlockSource> Blocksources { get; }
        }

        public interface ILocalBlockSource
        {
            string TargetPath { get; }
            long TargetFileID { get; }
            IEnumerable<IBlockDescriptor> Blocks { get; }
        }

        public interface IFileToRestore
        {
            string Path { get; }
            string Hash { get; }
            long ID { get; }
        }

        public interface IPatchBlock
        {
            long Offset { get; }
            long Size { get; }
            string Key { get; }
        }

        public interface IVolumePatch
        {
            string Path { get; }
            IEnumerable<IPatchBlock> Blocks { get; }
        }

        public IEnumerable<IExistingFile> GetExistingFilesWithBlocks()
        {
            return new ExistingFileEnumerable(m_connection, m_tempfiletable);
        }

        public IEnumerable<ILocalBlockSource> GetFilesAndSourceBlocks()
        {
            return new LocalBlockSourceEnumerable(m_connection, m_tempfiletable, m_tempblocktable, m_blocksize);
        }

        public IList<IRemoteVolume> GetMissingVolumes()
        {
            using (var cmd = m_connection.CreateCommand())
            {
                List<IRemoteVolume> result = new List<IRemoteVolume>();
                cmd.CommandText = string.Format(@"SELECT DISTINCT ""RemoteVolume"".""Name"", ""RemoteVolume"".""Hash"", ""RemoteVolume"".""Size"" FROM ""RemoteVolume"" WHERE ""ID"" IN (SELECT DISTINCT ""Block"".""VolumeID"" FROM ""Block"" WHERE ""Block"".""Hash"" IN (SELECT DISTINCT ""{0}"".""Hash"" FROM ""{0}"" WHERE ""{0}"".""Restored"" = 0))", m_tempblocktable);
                using (var rd = cmd.ExecuteReader())
                {
                    object[] r = new object[3];
                    while (rd.Read())
                    {
                        rd.GetValues(r);
                        result.Add(new RemoteVolume(
                            r[0] == null ? null : r[0].ToString(),
                            r[1] == null ? null : r[1].ToString(),
                            r[2] == null ? -1 : Convert.ToInt64(r[2])
                        ));
                    }
                }

                return result;
            }
        }

        public IEnumerable<IVolumePatch> GetFilesWithMissingBlocks(BlockVolumeReader curvolume)
        {
            return new VolumePatchEnumerable(m_connection, m_tempfiletable, m_tempblocktable, m_blocksize, curvolume);
        }

		private class FileToRestore : IFileToRestore
		{
			public string Path { get; private set; }
			public string Hash { get; private set; }
			public long ID { get; private set; }
			
			public FileToRestore(long id, string path, string hash)
			{
				this.ID = id;
				this.Path = path;
				this.Hash = hash;
			}
		}

        public IEnumerable<IFileToRestore> GetFilesToRestore()
        {
            using(var cmd = m_connection.CreateCommand())
            using(var rd = cmd.ExecuteReader(string.Format(@"SELECT ""{0}"".""ID"", ""{0}"".""TargetPath"", ""Blockset"".""FullHash"" FROM ""{0}"",""Blockset"" WHERE ""{0}"".""BlocksetID"" = ""Blockset"".""ID"" ", m_tempfiletable)))
                while (rd.Read())
                	yield return new FileToRestore(Convert.ToInt64(rd.GetValue(0)), rd.GetValue(1).ToString(), rd.GetValue(2).ToString());
        }

        public void DropRestoreTable()
        {
            using (var cmd = m_connection.CreateCommand())
            {
                if (m_tempfiletable != null)
                    try
                    {
                        cmd.CommandText = string.Format(@"DROP TABLE ""{0}""", m_tempfiletable);
                        cmd.ExecuteNonQuery();
                    }
                    finally { m_tempfiletable = null; }

                if (m_tempblocktable != null)
                    try
                    {
                        cmd.CommandText = string.Format(@"DROP TABLE ""{0}""", m_tempblocktable);
                        cmd.ExecuteNonQuery();
                    }
                    finally { m_tempblocktable = null; }
            }
        }

        public interface IBlockMarker : IDisposable
        {
            void SetBlockRestored(long targetfileid, long index, string hash, long blocksize);
            void Commit();
            System.Data.IDbTransaction Transaction { get; }
        }

        private class BlockMarker : IBlockMarker
        {
            private System.Data.IDbCommand m_insertblockCommand;
            private System.Data.IDbCommand m_insertfileCommand;
            private string m_updateTable;
            private string m_blocktablename;
            
            public System.Data.IDbTransaction Transaction { get { return m_insertblockCommand.Transaction; } }

            public BlockMarker(System.Data.IDbConnection connection, string blocktablename, string filetablename)
            {
                m_insertblockCommand = connection.CreateCommand();
                m_insertblockCommand.Transaction = connection.BeginTransaction();
                
                m_insertfileCommand = connection.CreateCommand();
                m_insertfileCommand.Transaction = m_insertblockCommand.Transaction;
                
                m_blocktablename = blocktablename;
                m_updateTable = "UpdatedBlocks-" + Library.Utility.Utility.ByteArrayAsHexString(Guid.NewGuid().ToByteArray());
                
                m_insertblockCommand.ExecuteNonQuery(string.Format(@"CREATE TEMPORARY TABLE ""{0}"" (""FileID"" INTEGER NOT NULL, ""Index"" INTEGER NOT NULL, ""Hash"" TEXT NOT NULL, ""Size"" INTEGER NOT NULL)", m_updateTable));
                m_insertblockCommand.CommandText = string.Format(@"INSERT INTO ""{0}"" (""FileID"", ""Index"", ""Hash"", ""Size"") VALUES (?, ?, ?, ?) ", m_updateTable);
                m_insertblockCommand.AddParameters(4);
                
                m_insertfileCommand.CommandText = string.Format(@"INSERT INTO ""{1}"" (""FileID"", ""Index"", ""Hash"", ""Size"") SELECT ""FileID"", ""Index"", ""Hash"", ""Size"" FROM ""{0}"" WHERE ""{0}"".""FileID"" = ? ", m_blocktablename, m_updateTable);
                m_insertfileCommand.AddParameters(1);
            }

            public void SetBlockRestored(long targetfileid, long index, string hash, long size)
            {
                m_insertblockCommand.SetParameterValue(0, targetfileid);
                m_insertblockCommand.SetParameterValue(1, index);
                m_insertblockCommand.SetParameterValue(2, hash);
                m_insertblockCommand.SetParameterValue(3, size);
                var r = m_insertblockCommand.ExecuteNonQuery();
                if (r != 1)
                    throw new Exception("Unexpected insert result");
            }
            
            public void Commit()
            {
            	m_insertblockCommand.Parameters.Clear();
            	var rc = m_insertblockCommand.ExecuteNonQuery(string.Format(@"UPDATE ""{0}"" SET ""Restored"" = 1 WHERE ""ID"" IN (SELECT ""{0}"".""ID"" FROM ""{0}"", ""{1}"" WHERE ""{0}"".""FileID"" = ""{1}"".""FileID"" AND ""{0}"".""Index"" = ""{1}"".""Index"" AND ""{0}"".""Hash"" = ""{1}"".""Hash"" AND ""{0}"".""Size"" = ""{1}"".""Size"" )", m_blocktablename, m_updateTable));
            	var nc = Convert.ToInt64(m_insertblockCommand.ExecuteScalar(string.Format(@"SELECT COUNT(*) FROM ""{0}"" ", m_updateTable)));
            
        		m_insertblockCommand.ExecuteNonQuery(string.Format(@"DROP TABLE ""{0}"" ", m_updateTable));
        		m_updateTable = null;
        		
        		if (rc != nc)
        			throw new Exception(string.Format("Inconsistency while marking blocks as updated. Updated blocks: {0}, Registered blocks: {1}", rc, nc));

                var tr = m_insertblockCommand.Transaction;
                m_insertblockCommand.Dispose();
                m_insertblockCommand = null;
                using(new Logging.Timer("CommitBlockMarker"))
                    tr.Commit();
                tr.Dispose();
            }

            public void Dispose()
            {
	            if (m_updateTable != null)
	            {
	            	try 
	            	{
	            		m_insertblockCommand.Parameters.Clear();
	            		m_insertblockCommand.ExecuteNonQuery(string.Format(@"DROP TABLE ""{0}"" ", m_updateTable));
	            	}
	            	catch { }
	            	finally { m_updateTable = null; }
	            }
            
                if (m_insertblockCommand != null)
                {
                    var t = m_insertblockCommand;
                    m_insertblockCommand = null;
                    t.Dispose();
                }
            }
        }

        public IBlockMarker CreateBlockMarker()
        {
            return new BlockMarker(m_connection, m_tempblocktable, m_tempfiletable);
        }

        public override void Dispose()
        {
            base.Dispose();
            DropRestoreTable();
        }

        public IEnumerable<string> GetTargetFolders()
        {
            using (var cmd = m_connection.CreateCommand())
            using (var rd = cmd.ExecuteReader(string.Format(@"SELECT ""TargetPath"" FROM ""{0}"" WHERE ""BlocksetID"" == ?", m_tempfiletable), FOLDER_BLOCKSET_ID))
            	while(rd.Read())
            		yield return rd.GetValue(0).ToString();
        }

		public interface IFastSource
		{
			string TargetPath { get; }
			long TargetFileID { get; }
			string SourcePath { get; }
			IEnumerable<IBlockEntry> Blocks { get; }
		}
		
		public interface IBlockEntry
		{
			long Offset { get; }
			long Size { get; }
			long Index { get; }
			string Hash { get; }
		}

		private class FastSource : IFastSource
		{
			private class BlockEntry : IBlockEntry
			{
				private System.Data.IDataReader m_rd;
				private long m_blocksize;
				public BlockEntry(System.Data.IDataReader rd, long blocksize) { m_rd = rd; m_blocksize = blocksize; }
				public long Offset { get { return Convert.ToInt64(m_rd.GetValue(3)) * m_blocksize; } }
				public long Index { get { return Convert.ToInt64(m_rd.GetValue(3)); } }
				public long Size { get { return Convert.ToInt64(m_rd.GetValue(5)); } }
				public string Hash { get { return m_rd.GetValue(4).ToString(); } }
			}
		
			private System.Data.IDataReader m_rd;
			private long m_blocksize;
			public FastSource(System.Data.IDataReader rd, long blocksize) { m_rd = rd; m_blocksize = blocksize; MoreData = true; }
			public bool MoreData { get; private set; }
			public string TargetPath { get { return m_rd.GetValue(0).ToString(); } }
			public long TargetFileID { get { return Convert.ToInt64(m_rd.GetValue(2)); } }
			public string SourcePath { get { return m_rd.GetValue(1).ToString(); } }
			
			public IEnumerable<IBlockEntry> Blocks
			{
				get
				{
					var tid = this.TargetFileID;
					
					do
					{
						yield return new BlockEntry(m_rd, m_blocksize);
					} while((MoreData = m_rd.Read()) && tid == this.TargetFileID);
					
				}
			}
		}

		public IEnumerable<IFastSource> GetFilesAndSourceBlocksFast()
		{
			var whereclause = string.Format(@" ""{0}"".""ID"" = ""{1}"".""FileID"" AND ""{1}"".""Restored"" = 0 AND ""{0}"".""TargetPath"" != ""{0}"".""Path"" ", m_tempfiletable, m_tempblocktable);		
			var sourepaths = string.Format(@"SELECT DISTINCT ""{0}"".""Path"" FROM ""{0}"", ""{1}"" WHERE " + whereclause, m_tempfiletable, m_tempblocktable);
			var latestBlocksetIds = @"SELECT ""File"".""Path"", ""File"".""BlocksetID"", MAX(""FilesetEntry"".""Scantime"") FROM ""FilesetEntry"", ""File"" WHERE ""FilesetEntry"".""FileID"" = ""File"".""ID"" AND ""File"".""Path"" IN (" + sourepaths + @") GROUP BY ""File"".""Path"" ";
			var sources = string.Format(@"SELECT DISTINCT ""{0}"".""TargetPath"", ""{0}"".""Path"", ""{0}"".""ID"", ""{1}"".""Index"", ""{1}"".""Hash"", ""{1}"".""Size"" FROM ""{0}"", ""{1}"", ""File"", (" + latestBlocksetIds + @") S, ""Block"", ""BlocksetEntry"" WHERE ""BlocksetEntry"".""BlocksetID"" = ""S"".""BlocksetID"" AND ""BlocksetEntry"".""BlocksetID"" = ""File"".""BlocksetID"" AND ""BlocksetEntry"".""BlockID"" = ""Block"".""ID"" AND ""{1}"".""Index"" = ""BlocksetEntry"".""Index"" AND ""{1}"".""Hash"" = ""Block"".""Hash"" AND ""{1}"".""Size"" = ""Block"".""Size"" AND ""S"".""Path"" = ""{0}"".""Path"" AND " + whereclause + @" ORDER BY ""{0}"".""ID"", ""{1}"".""Index"" ", m_tempfiletable, m_tempblocktable);
			using(var cmd = m_connection.CreateCommand())
			using(var rd = cmd.ExecuteReader(sources))
			{
				if (rd.Read())
				{
					var more = false;
					do
					{
						var n = new FastSource(rd, m_blocksize);
						var tid = n.TargetFileID;
						yield return n;
						
						more = n.MoreData;
						while(more && n.TargetFileID == tid)
							more = rd.Read();

					} while (more);
				}
			}	
		}

    }
}
