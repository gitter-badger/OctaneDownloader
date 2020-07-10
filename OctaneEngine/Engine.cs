﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

// ReSharper disable All

namespace OctaneDownloadEngine
{
    public class OctaneEngine
    {
        protected OctaneEngine()
        {
            ServicePointManager.DefaultConnectionLimit = 10000;
        }

        static int TasksDone = 0;

        public async static void DownloadByteArray(string url, double parts, Action<byte[]> callback,
            Action<int> progressCallback = null)
        {
            var responseLength = (await WebRequest.Create(url).GetResponseAsync()).ContentLength;
            var partSize = (long) Math.Floor(responseLength / parts);
            var pieces = new List<FileChunk>();

            Console.WriteLine(responseLength.ToString(CultureInfo.InvariantCulture) + " TOTAL SIZE");
            Console.WriteLine(partSize.ToString(CultureInfo.InvariantCulture) + " PART SIZE" + "\n");


            try
            {
                //Using our custom progressbar
                using (var progress = new ProgressBar())
                {
                    using (var ms = new MemoryStream())
                    {
                        ms.SetLength(responseLength);

                        //Using custom concurrent queue to implement Enqueue and Dequeue Events
                        var asyncTasks = new EventfulConcurrentQueue<FileChunk>();

                        //Delegate for Dequeue
                        asyncTasks.ItemDequeued += delegate
                        {
                            //Tasks done holds the count of the tasks done
                            //Parts *2 because there are parts number of Enqueue AND Dequeue operations
                            if (progressCallback == null)
                            {
                                progress.Report(TasksDone / (parts * 2));
                                Thread.Sleep(20);
                            }
                            else
                            {
                                progressCallback?.Invoke(Convert.ToInt32(TasksDone / (parts * 2)));
                            }
                        };

                        //Delagate for Enqueue
                        asyncTasks.ItemEnqueued += delegate
                        {
                            if (progressCallback == null)
                            {
                                progress.Report(TasksDone / (parts * 2));
                                Thread.Sleep(20);
                            }
                            else
                            {
                                progressCallback?.Invoke(Convert.ToInt32(TasksDone / (parts * 2)));
                            }
                        };

                        // GetResponseAsync deadlocks for some reason so switched to HttpClient instead
                        var client = new HttpClient(
                            //Use our custom Retry handler, with a max retry value of 10
                            new RetryHandler(new HttpClientHandler(), 10)) {MaxResponseContentBufferSize = 1000000000};

                        //Variable to hold the old loop end
                        var previous = 0;

                        //Loop to add all the events to the queue
                        for (var i = (int) partSize; i <= responseLength; i += (int) partSize)
                        {
                            Console.Title = "WRITING CHUNKS...";
                            if (i + partSize < responseLength)
                            {
                                //Start and end values for the chunk
                                var start = previous;
                                var current_end = i;

                                pieces.Add(new FileChunk(start, current_end));

                                //Set the start of the next loop to be the current end
                                previous = current_end;
                            }
                            else
                            {
                                //Start and end values for the chunk
                                var start = previous;
                                var current_end = i;

                                pieces.Add(new FileChunk(start, (int) responseLength));

                                //Set the start of the next loop to be the current end
                                previous = current_end;
                            }
                        }

                        var getStream = new TransformBlock<FileChunk, Tuple<Task<HttpResponseMessage>, FileChunk>>(
                            piece =>
                            {
                                Console.Title = "STREAMING....";
                                //Open a http request with the range
                                var request = new HttpRequestMessage {RequestUri = new Uri(url)};
                                request.Headers.Range = new RangeHeaderValue(piece.start, piece.end);

                                //Send the request
                                var downloadTask = client.SendAsync(request, HttpCompletionOption.ResponseContentRead);

                                //Use interlocked to increment Tasks done by one
                                Interlocked.Add(ref TasksDone, 1);
                                asyncTasks.Enqueue(piece);

                                return new Tuple<Task<HttpResponseMessage>, FileChunk>(downloadTask, piece);
                            }, new ExecutionDataflowBlockOptions
                            {
                                BoundedCapacity = (int) parts, // Cap the item count
                                MaxDegreeOfParallelism = Environment.ProcessorCount, // Parallelize on all cores
                            }
                        );

                        var writeStream = new ActionBlock<Tuple<Task<HttpResponseMessage>, FileChunk>>(async tuple =>
                        {
                            var buffer = new byte[tuple.Item2.end - tuple.Item2.start];
                            using (var stream = await tuple.Item1.Result.Content.ReadAsStreamAsync())
                            {
                                await stream.ReadAsync(buffer, 0, buffer.Length);
                            }

                            lock (ms)
                            {
                                ms.Position = tuple.Item2.start;
                                ms.Write(buffer, 0, buffer.Length);
                            }

                            var s = new FileChunk();
                            asyncTasks.TryDequeue(out s);
                            Interlocked.Add(ref TasksDone, 1);
                        }, new ExecutionDataflowBlockOptions
                        {
                            BoundedCapacity = (int) parts, // Cap the item count
                            MaxDegreeOfParallelism = Environment.ProcessorCount, // Parallelize on all cores
                        });

                        var linkOptions = new DataflowLinkOptions {PropagateCompletion = true};
                        getStream.LinkTo(writeStream, linkOptions);

                        Parallel.ForEach(pieces, async (chunk) => { await getStream.SendAsync(chunk); });

                        getStream.Complete();
                        await writeStream.Completion;

                        if (asyncTasks.Count == 0)
                        {
                            ms.Flush();
                            ms.Close();
                            callback?.Invoke(ms.ToArray());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void CombineMultipleFilesIntoSingleFile(List<FileChunk> files, string outputFilePath)
        {
            Console.Title = string.Format("Number of files: {0}.", files.Count);
            using (var outputStream = File.Create(outputFilePath))
            {
                foreach (var inputFilePath in files)
                {
                    using (var inputStream = File.OpenRead(inputFilePath._tempfilename))
                    {
                        // Buffer size can be passed as the second argument.
                        outputStream.Position = inputFilePath.start;
                        inputStream.CopyTo(outputStream);
                    }

                    Console.Title = string.Format("The file has been processed from {0} to {1}.", inputFilePath.start,
                        inputFilePath.end);
                    File.Delete(inputFilePath._tempfilename);
                }
            }
        }

        public static bool IsFileReady(string filename)
        {
            // If the file can be opened for exclusive access it means that the file
            // is no longer locked by another process.
            try
            {
                using (FileStream inputStream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.None))
                    return inputStream.Length > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public async static Task DownloadFile(string url, double parts, string outFile = null,
            Action<int> progressCallback = null)
        {
            var responseLength = (await WebRequest.Create(url).GetResponseAsync()).ContentLength;
            var partSize = (long) Math.Round(responseLength / parts);
            var pieces = new List<FileChunk>();

            Console.WriteLine(responseLength.ToString(CultureInfo.InvariantCulture) + " TOTAL SIZE");
            Console.WriteLine(partSize.ToString(CultureInfo.InvariantCulture) + " PART SIZE" + "\n");

            var filename = outFile;

            var defaultTitle = Console.Title;
            var uri = new Uri(url);

            if (outFile == null)
            {
                filename = Path.GetFileName(uri.LocalPath);
            }
            else
            {
                filename = outFile;
            }

            try
            {
                //Using our custom progressbar
                using (var progress = new ProgressBar())
                {
                    //Using custom concurrent queue to implement Enqueue and Dequeue Events
                    var asyncTasks = new EventfulConcurrentQueue<FileChunk>();

                    //Delegate for Dequeue
                    asyncTasks.ItemDequeued += delegate
                    {
                        //Tasks done holds the count of the tasks done
                        //Parts *2 because there are parts number of Enqueue AND Dequeue operations
                        if (progressCallback == null)
                        {
                            progress.Report(TasksDone / (parts * 2));
                            Thread.Sleep(20);
                        }
                        else
                        {
                            progressCallback?.Invoke(Convert.ToInt32(TasksDone / (parts * 2)));
                        }
                    };

                    //Delagate for Enqueue
                    asyncTasks.ItemEnqueued += delegate
                    {
                        if (progressCallback == null)
                        {
                            progress.Report(TasksDone / (parts * 2));
                            Thread.Sleep(20);
                        }
                        else
                        {
                            progressCallback?.Invoke(Convert.ToInt32(TasksDone / (parts * 2)));
                        }
                    };

                    // GetResponseAsync deadlocks for some reason so switched to HttpClient instead
                    var client = new HttpClient(
                            //Use our custom Retry handler, with a max retry value of 10
                            new RetryHandler(new HttpClientHandler(), 10))
                        {MaxResponseContentBufferSize = 1000000000};

                    //Variable to hold the old loop end
                    var previous = 0;

                    //Loop to add all the events to the queue
                    for (var i = (int) partSize; i <= responseLength; i += (int) partSize)
                    {
                        Console.Title = "WRITING CHUNKS...";
                        if (i + partSize < responseLength)
                        {
                            //Start and end values for the chunk
                            var start = previous;
                            var current_end = i;

                            pieces.Add(new FileChunk(start, current_end));

                            //Set the start of the next loop to be the current end
                            previous = current_end;
                        }
                        else
                        {
                            //Start and end values for the chunk
                            var start = previous;
                            var current_end = i;

                            pieces.Add(new FileChunk(start, (int) responseLength));

                            //Set the start of the next loop to be the current end
                            previous = current_end;
                        }
                    }

                    Console.Title = "CHUNKS DONE";

                    var getStream = new TransformBlock<FileChunk, Tuple<Task<HttpResponseMessage>, FileChunk>>(piece =>
                        {
                            Console.Title = "STREAMING....";
                            //Open a http request with the range
                            var request = new HttpRequestMessage {RequestUri = new Uri(url)};
                            request.Headers.Range = new RangeHeaderValue(piece.start, piece.end);

                            //Send the request
                            var downloadTask = client.SendAsync(request, HttpCompletionOption.ResponseContentRead);

                            //Use interlocked to increment Tasks done by one
                            Interlocked.Add(ref TasksDone, 1);
                            asyncTasks.Enqueue(piece);

                            return new Tuple<Task<HttpResponseMessage>, FileChunk>(downloadTask, piece);
                        }, new ExecutionDataflowBlockOptions
                        {
                            BoundedCapacity = (int) parts, // Cap the item count
                            MaxDegreeOfParallelism = Environment.ProcessorCount, // Parallelize on all cores
                        }
                    );

                    var writeStream = new ActionBlock<Tuple<Task<HttpResponseMessage>, FileChunk>>(async task =>
                    {
                        using (var streamToRead = await task.Item1.Result.Content.ReadAsStreamAsync())
                        {
                            using (var fileToWriteTo = File.Open(task.Item2._tempfilename, FileMode.OpenOrCreate,
                                FileAccess.ReadWrite, FileShare.ReadWrite))
                            {
                                await streamToRead.CopyToAsync(fileToWriteTo);
                                var s = new FileChunk();
                                asyncTasks.TryDequeue(out s);
                                Interlocked.Add(ref TasksDone, 1);
                            }
                        }
                    }, new ExecutionDataflowBlockOptions
                    {
                        BoundedCapacity = (int) parts, // Cap the item count
                        MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded, // Parallelize on all cores
                    });

                    //Propage errors and completion
                    var linkOptions = new DataflowLinkOptions {PropagateCompletion = true};
                    //Write the block ass soon as the stream is recieved
                    getStream.LinkTo(writeStream, linkOptions);

                    //Start every piece in pieces in parallel fashion
                    Parallel.ForEach(pieces, async (chunk) => { await getStream.SendAsync(chunk); });

                    //Wait for all the streams to be recieved
                    getStream.Complete();
                    //Write all the streams
                    await writeStream.Completion;

                    //If all the tasks are done, Join the temp files
                    if (asyncTasks.Count == 0)
                    {
                        CombineMultipleFilesIntoSingleFile(pieces, filename);
                    }
                }

                //Restore the original title
                Console.Title = defaultTitle;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);

                //Delete the tempfiles if there's an error
                foreach (var piece in pieces)
                {
                    try
                    {
                        File.Delete(piece._tempfilename);
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }
        }

        public async static Task DownloadFiles(List<string> urls, List<string> outfiles, double parts,
            Action<int> progressCallback = null)
        {
            Parallel.For(0, urls.Count(), async i =>
            {
                await DownloadFile(urls[i], parts, outfiles[i], progressCallback);
            });
        }
    }
}