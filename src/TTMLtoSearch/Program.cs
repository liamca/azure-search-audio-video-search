﻿using CsvHelper;
using Microsoft.Azure.Search;
using Microsoft.Azure.Search.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Linq;
using System.Xml.Linq;

namespace TTMLtoSearch
{
    class Program
    {
        private static string searchServiceName = [azure search service];
        private static string apiKey = [azure search service api key];
        private static SearchServiceClient _searchClient;
        private static SearchIndexClient _indexClient;
        private static string AzureSearchIndex = "buildsessions";
        private static readonly XNamespace ttmlns = "http://www.w3.org/ns/ttml";

        static void Main(string[] args)
        {

            // Create an HTTP reference to the catalog index
            _searchClient = new SearchServiceClient(searchServiceName, new SearchCredentials(apiKey));
            _indexClient = _searchClient.Indexes.GetClient(AzureSearchIndex);

            Console.WriteLine("{0}", "Deleting index...\n");
            DeleteIndex();
            
            Console.WriteLine("{0}", "Creating index...\n");
            if (CreateIndex() == false)
            {
                Console.ReadLine();
                return;
            }

            Console.WriteLine("{0}", "Uploading video metadata...\n");
            UploadMetadata();

            // Execute a search for the term 'Azure Search' which will only return one result
            Console.WriteLine("{0}", "Searching for videos about 'Azure Search'...\n");
            DocumentSearchResult results = SearchIndex("'Azure Search'");
            foreach (var doc in results.Results)
                Console.WriteLine("Found Session: {0}", doc.Document["session_title"]);

            Console.WriteLine("{0}", "\nMerging in transcribed text from videos...\n");
            MergeTranscribedText();

            // Execute a search for the term 'Azure Search' which will return multiple results
            Console.WriteLine("{0}", "Searching for videos about 'Azure Search'...\n");
            results = SearchIndex("'Azure Search'");
            foreach (var doc in results.Results)
                Console.WriteLine("Found Session: {0}", doc.Document["session_title"]);

            Console.WriteLine("\nPress any key to continue\n");
            Console.ReadLine();

        }

        private static bool DeleteIndex()
        {
            // Delete the index, data source, and indexer.
            try
            {
                _searchClient.Indexes.Delete(AzureSearchIndex);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error deleting index: {0}\r\n", ex.Message);
                Console.WriteLine("Did you remember to set your SearchServiceName and SearchServiceApiKey?\r\n");
                return false;
            }

            return true;
        }
        private static bool CreateIndex()
        {
            // Create the Azure Search index based on the included schema
            try
            {
                var definition = new Index()
                {
                    Name = AzureSearchIndex,
                    Fields = new[] 
                    { 
                        new Field("session_id",     DataType.String)         { IsKey = true,  IsSearchable = false, IsFilterable = false, IsSortable = false, IsFacetable = false, IsRetrievable = true},
                        new Field("session_title",  DataType.String)         { IsKey = false, IsSearchable = true,  IsFilterable = true,  IsSortable = true,  IsFacetable = false, IsRetrievable = true, Analyzer = AnalyzerName.EnMicrosoft},
                        new Field("tags",           DataType.String)         { IsKey = false, IsSearchable = true,  IsFilterable = true,  IsSortable = true,  IsFacetable = false, IsRetrievable = true, Analyzer = AnalyzerName.EnMicrosoft},
                        new Field("speakers",       DataType.String)         { IsKey = false, IsSearchable = true,  IsFilterable = true,  IsSortable = true,  IsFacetable = false, IsRetrievable = true, Analyzer = AnalyzerName.EnMicrosoft},
                        new Field("date",           DataType.String)         { IsKey = false, IsSearchable = true,  IsFilterable = true,  IsSortable = true,  IsFacetable = false, IsRetrievable = true},
                        new Field("url",            DataType.String)         { IsKey = false, IsSearchable = false, IsFilterable = false, IsSortable = false, IsFacetable = false, IsRetrievable = true},
                        new Field("transcribed_text",DataType.String)        { IsKey = false, IsSearchable = true,  IsFilterable = false,  IsSortable = false,  IsFacetable = false, IsRetrievable = true, Analyzer = AnalyzerName.EnMicrosoft},
                    }
                };
                _searchClient.Indexes.Create(definition);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error creating index: {0}\r\n", ex.Message);
                return false;
            }
            return true;

        }

        private static void UploadMetadata()
        {
            // Upload metadata on Build Sessions from a CSV file
            List<IndexAction> indexOperations = GetSessionsFromCSV(); 

            try
            {
                _indexClient.Documents.Index(new IndexBatch(indexOperations));
            }
            catch (IndexBatchException e)
            {
                // Sometimes when your Search service is under load, indexing will fail for some of the documents in
                // the batch. Depending on your application, you can take compensating actions like delaying and
                // retrying. For this simple demo, we just log the failed document keys and continue.
                Console.WriteLine(
                 "Failed to index some of the documents: {0}",
                        String.Join(", ", e.IndexingResults.Where(r => !r.Succeeded).Select(r => r.Key)));
            }

            // Wait a while for indexing to complete.
            Console.WriteLine("{0}", "Waiting 5 seconds for content to become searchable...\n");
            Thread.Sleep(5000);
        }

        private static void MergeTranscribedText()
        {
            // Upload metadata on Build Sessions from a CSV file
            List<IndexAction> indexOperations = new List<IndexAction>();
            string[] files = Directory.GetFiles(@"ttml");
            try
            {
                foreach (var file in files)
                {
                    var doc = new Document();
                    string session_id = file.Substring(file.IndexOf("\\") + 1).Replace(".mp3.ttml", "").ToLower();
                    doc.Add("session_id", ConvertToAlphaNumeric(session_id));
                    doc.Add("transcribed_text", ParseTTML(file));
                    indexOperations.Add(IndexAction.MergeOrUpload(doc));
                    if (indexOperations.Count >= 100)
                    {
                        Console.WriteLine("Indexing {0} transcriptions...\n", indexOperations.Count);
                        _indexClient.Documents.Index(new IndexBatch(indexOperations));
                        indexOperations.Clear();
                    }
                }
                if (indexOperations.Count > 0)
                {
                    Console.WriteLine("Indexing {0} transcriptions...\n", indexOperations.Count);
                    _indexClient.Documents.Index(new IndexBatch(indexOperations));
                }

            }
            catch (IndexBatchException e)
            {
                // Sometimes when your Search service is under load, indexing will fail for some of the documents in
                // the batch. Depending on your application, you can take compensating actions like delaying and
                // retrying. For this simple demo, we just log the failed document keys and continue.
                Console.WriteLine(
                 "Failed to index some of the documents: {0}",
                        String.Join(", ", e.IndexingResults.Where(r => !r.Succeeded).Select(r => r.Key)));


            }

            // Wait a while for indexing to complete.
            Console.WriteLine("{0}", "Waiting 5 seconds for content to become searchable...\n");
            Thread.Sleep(5000);
        }



        private static DocumentSearchResult SearchIndex(string searchText)
        {
            // Execute search based on query string
            try
            {
                SearchParameters sp = new SearchParameters() { SearchMode = SearchMode.All };
                return _indexClient.Documents.Search(searchText, sp);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error querying index: {0}\r\n", ex.Message.ToString());
            }
            return null;
        }
        private static List<IndexAction> GetSessionsFromCSV()
        {
            List<IndexAction> indexOperations = new List<IndexAction>();

            using (var sr = new StreamReader(@"BuildSessionMetatdata.csv"))
            {
                var reader = new CsvReader(sr);
                IEnumerable<DataRecord> records = reader.GetRecords<DataRecord>();

                foreach (DataRecord record in records)
                {
                    Document doc = new Document();
                    string title = record.session_title;
                    string session_id = title.Replace(" ", "_").ToLower();
                    doc.Add("session_id", ConvertToAlphaNumeric(session_id));
                    doc.Add("session_title", title);
                    doc.Add("tags", record.tags);
                    doc.Add("speakers", record.speakers);
                    doc.Add("date", Convert.ToDateTime(record.date).ToShortDateString());
                    doc.Add("url", record.url);
                    indexOperations.Add(IndexAction.Upload(doc));

                }
            }
            return indexOperations;
        }

        static string ConvertToAlphaNumeric(string plainText)
        {
            Regex rgx = new Regex("[^a-zA-Z0-9 -]");
            return rgx.Replace(plainText, "");
        }
        private static string ParseTTML(string ttmlFile)
        {
            // This will extract all the spoken text from a TTML file into a single string
            return string.Join("\r\n", XDocument.Load(ttmlFile)
                                                .Element(ttmlns + "tt")
                                                .Element(ttmlns + "body")
                                                .Element(ttmlns + "div")
                                                .Elements(ttmlns + "p")
                                                .Select(snippet => snippet.Value));
        }

    }
}
