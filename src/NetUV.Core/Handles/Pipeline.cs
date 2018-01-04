﻿// Copyright (c) Johnny Z. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace NetUV.Core.Handles
{
    using System;
    using System.Diagnostics;
    using System.Diagnostics.Contracts;
    using NetUV.Core.Buffers;
    using NetUV.Core.Channels;
    using NetUV.Core.Common;
    using NetUV.Core.Logging;
    using NetUV.Core.Native;
    using NetUV.Core.Requests;

    sealed class Pipeline : IDisposable
    {
        const int DefaultPoolSize = 1024;

        static readonly ILog Log = LogFactory.ForContext<Pipeline>();

        static readonly ThreadLocalPool<WriteRequest> Recycler = 
            new ThreadLocalPool<WriteRequest>(handle => new WriteRequest(handle), DefaultPoolSize);

        readonly StreamHandle streamHandle;
        readonly PooledByteBufferAllocator allocator;
        readonly ReceiveBufferSizeEstimate receiveBufferSizeEstimate;
        readonly PendingRead pendingRead;
        IStreamConsumer<StreamHandle> streamConsumer;

        internal Pipeline(StreamHandle streamHandle)
            : this(streamHandle, PooledByteBufferAllocator.Default)
        { }

        internal Pipeline(StreamHandle streamHandle, PooledByteBufferAllocator allocator)
        {
            Contract.Requires(streamHandle != null);
            Contract.Requires(allocator != null);

            this.streamHandle = streamHandle;
            this.allocator = allocator;
            this.receiveBufferSizeEstimate = new ReceiveBufferSizeEstimate();
            this.pendingRead = new PendingRead();
        }

        internal void Consumer(IStreamConsumer<StreamHandle> consumer)
        {
            Contract.Requires(consumer != null);

            this.streamConsumer = consumer;
        }

        internal WritableBuffer Allocate() => new WritableBuffer(this.allocator.Buffer());

        internal uv_buf_t AllocateReadBuffer()
        {
            IByteBuffer buffer = this.receiveBufferSizeEstimate.Allocate(this.allocator);
            if (Log.IsTraceEnabled)
            {
                Log.TraceFormat("{0} receive buffer allocated size = {1}", nameof(Pipeline), buffer.Capacity);
            }

            return this.pendingRead.GetBuffer(buffer);
        }

        internal IByteBuffer GetBuffer(ref uv_buf_t buf)
        {
            IByteBuffer byteBuffer = this.pendingRead.Buffer;
            this.pendingRead.Reset();
            return byteBuffer;
        }

        internal void OnReadCompleted(IByteBuffer byteBuffer, Exception error) => this.InvokeRead(byteBuffer, 0, error, true);

        internal void OnReadCompleted(IByteBuffer byteBuffer, int size)
        {
            Debug.Assert(byteBuffer != null && size >= 0);

            this.receiveBufferSizeEstimate.Record(size);
            this.InvokeRead(byteBuffer, size);
        }

        void InvokeRead(IByteBuffer byteBuffer, int size, Exception error = null, bool completed = false)
        {
            if (size <= 0)
            {
                byteBuffer.Release();
            }

            ReadableBuffer buffer = size > 0  
                ? new ReadableBuffer(byteBuffer, size) 
                : ReadableBuffer.Empty;

            var completion = new StreamReadCompletion(ref buffer,  error, completed);
            try
            {
                this.streamConsumer?.Consume(this.streamHandle, completion);
            }
            catch (Exception exception)
            {
                Log.Warn($"{nameof(Pipeline)} Exception whilst invoking read callback.", exception);
            }
            finally
            {
                completion.Dispose();
            }
        }

        internal void QueueWrite(WriteBufferRef bufferRef, Action<StreamHandle, Exception> completion)
        {
            Contract.Requires(bufferRef != null);

            try
            {
                WriteRequest request = Recycler.Take();
                request.Prepare(bufferRef, 
                    (writeRequest, exception) => completion?.Invoke(this.streamHandle, exception));

                this.streamHandle.WriteStream(request);
            }
            catch (Exception exception)
            {
                Log.Error($"{nameof(Pipeline)} {this.streamHandle.HandleType} faulted.", exception);
                throw;
            }
        }

        internal void QueueWrite(WriteBufferRef bufferRef, StreamHandle sendHandle, Action<StreamHandle, Exception> completion)
        {
            Contract.Requires(bufferRef != null);
            Contract.Requires(sendHandle != null);

            try
            {
                WriteRequest request = Recycler.Take();
                request.Prepare(bufferRef,
                    (writeRequest, exception) => completion?.Invoke(this.streamHandle, exception));

                this.streamHandle.WriteStream(request, sendHandle);
            }
            catch (Exception exception)
            {
                Log.Error($"{nameof(Pipeline)} {this.streamHandle.HandleType} faulted.", exception);
                throw;
            }
        }

        public void Dispose()
        {
            this.pendingRead.Dispose();
            this.streamConsumer = null;
        }

        sealed class StreamReadCompletion : ReadCompletion, IStreamReadCompletion
        {
            internal StreamReadCompletion(ref ReadableBuffer data, Exception error, bool completed) 
                : base(ref data, error)
            {
                this.Completed = completed;
            }

            public bool Completed { get; }
        }
    }
}
