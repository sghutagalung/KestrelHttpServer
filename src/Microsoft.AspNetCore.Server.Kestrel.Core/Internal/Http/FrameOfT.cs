// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http
{
    public class Frame<TContext> : Frame
    {
        private readonly IHttpApplication<TContext> _application;

        public Frame(IHttpApplication<TContext> application, FrameContext frameContext)
            : base(frameContext)
        {
            _application = application;
        }

        /// <summary>
        /// Primary loop which consumes socket input, parses it for protocol framing, and invokes the
        /// application delegate for as long as the socket is intended to remain open.
        /// The resulting Task from this loop is preserved in a field which is used when the server needs
        /// to drain and close all currently active connections.
        /// </summary>
        public override async Task RequestProcessingAsync()
        {
            try
            {
                while (!_requestProcessingStopping)
                {
                    TimeoutControl.SetTimeout(_keepAliveTicks, TimeoutAction.CloseConnection);

                    InitializeHeaders();

                    while (!_requestProcessingStopping)
                    {
                        var result = await Input.ReadAsync();
                        var examined = result.Buffer.End;
                        var consumed = result.Buffer.End;

                        try
                        {
                            ParseRequest(result.Buffer, out consumed, out examined);
                        }
                        catch (InvalidOperationException)
                        {
                            if (_requestProcessingStatus == RequestProcessingStatus.ParsingHeaders)
                            {
                                throw BadHttpRequestException.GetException(RequestRejectionReason
                                    .MalformedRequestInvalidHeaders);
                            }
                            throw;
                        }
                        finally
                        {
                            Input.Advance(consumed, examined);
                        }

                        if (_requestProcessingStatus == RequestProcessingStatus.AppStarted)
                        {
                            break;
                        }

                        if (result.IsCompleted)
                        {
                            switch (_requestProcessingStatus)
                            {
                                case RequestProcessingStatus.RequestPending:
                                    return;
                                case RequestProcessingStatus.ParsingRequestLine:
                                    throw BadHttpRequestException.GetException(
                                        RequestRejectionReason.InvalidRequestLine);
                                case RequestProcessingStatus.ParsingHeaders:
                                    throw BadHttpRequestException.GetException(RequestRejectionReason
                                        .MalformedRequestInvalidHeaders);
                            }
                        }
                    }

                    if (!_requestProcessingStopping)
                    {
                        EnsureHostHeaderExists();

                        var messageBody = MessageBody.For(_httpVersion, FrameRequestHeaders, this);
                        _keepAlive = messageBody.RequestKeepAlive;
                        _upgrade = messageBody.RequestUpgrade;

                        InitializeStreams(messageBody);

                        var context = _application.CreateContext(this);
                        try
                        {
                            try
                            {
                                KestrelEventSource.Log.RequestStart(this);

                                await _application.ProcessRequestAsync(context).ConfigureAwait(false);

                                if (Volatile.Read(ref _requestAborted) == 0)
                                {
                                    VerifyResponseContentLength();
                                }
                            }
                            catch (Exception ex)
                            {
                                ReportApplicationError(ex);

                                if (ex is BadHttpRequestException)
                                {
                                    throw;
                                }
                            }
                            finally
                            {
                                KestrelEventSource.Log.RequestStop(this);

                                // Trigger OnStarting if it hasn't been called yet and the app hasn't
                                // already failed. If an OnStarting callback throws we can go through
                                // our normal error handling in ProduceEnd.
                                // https://github.com/aspnet/KestrelHttpServer/issues/43
                                if (!HasResponseStarted && _applicationException == null && _onStarting != null)
                                {
                                    await FireOnStarting();
                                }

                                PauseStreams();

                                if (_onCompleted != null)
                                {
                                    await FireOnCompleted();
                                }
                            }

                            // If _requestAbort is set, the connection has already been closed.
                            if (Volatile.Read(ref _requestAborted) == 0)
                            {
                                ResumeStreams();

                                if (HasResponseStarted)
                                {
                                    // If the response has already started, call ProduceEnd() before
                                    // consuming the rest of the request body to prevent
                                    // delaying clients waiting for the chunk terminator:
                                    //
                                    // https://github.com/dotnet/corefx/issues/17330#issuecomment-288248663
                                    //
                                    // ProduceEnd() must be called before _application.DisposeContext(), to ensure
                                    // HttpContext.Response.StatusCode is correctly set when
                                    // IHttpContextFactory.Dispose(HttpContext) is called.
                                    await ProduceEnd();
                                }

                                if (_keepAlive)
                                {
                                    // Finish reading the request body in case the app did not.
                                    await messageBody.Consume();
                                }

                                if (!HasResponseStarted)
                                {
                                    await ProduceEnd();
                                }
                            }
                            else if (!HasResponseStarted)
                            {
                                // If the request was aborted and no response was sent, there's no
                                // meaningful status code to log.
                                StatusCode = 0;
                            }
                        }
                        catch (BadHttpRequestException ex)
                        {
                            // Handle BadHttpRequestException thrown during app execution or remaining message body consumption.
                            // This has to be caught here so StatusCode is set properly before disposing the HttpContext
                            // (DisposeContext logs StatusCode).
                            SetBadRequestState(ex);
                        }
                        finally
                        {
                            _application.DisposeContext(context, _applicationException);

                            // StopStreams should be called before the end of the "if (!_requestProcessingStopping)" block
                            // to ensure InitializeStreams has been called.
                            StopStreams();
                        }
                    }

                    if (!_keepAlive)
                    {
                        // End the connection for non keep alive as data incoming may have been thrown off
                        return;
                    }

                    // Don't reset frame state if we're exiting the loop. This avoids losing request rejection
                    // information (for 4xx response), and prevents ObjectDisposedException on HTTPS (ODEs
                    // will be thrown if PrepareRequest is not null and references objects disposed on connection
                    // close - see https://github.com/aspnet/KestrelHttpServer/issues/1103#issuecomment-250237677).
                    if (!_requestProcessingStopping)
                    {
                        Reset();
                    }
                }
            }
            catch (BadHttpRequestException ex)
            {
                // Handle BadHttpRequestException thrown during request line or header parsing.
                // SetBadRequestState logs the error.
                SetBadRequestState(ex);
            }
            catch (ConnectionResetException ex)
            {
                // Don't log ECONNRESET errors made between requests. Browsers like IE will reset connections regularly.
                if (_requestProcessingStatus != RequestProcessingStatus.RequestPending)
                {
                    Log.RequestProcessingError(ConnectionId, ex);
                }
            }
            catch (IOException ex)
            {
                Log.RequestProcessingError(ConnectionId, ex);
            }
            catch (Exception ex)
            {
                Log.LogWarning(0, ex, "Connection processing ended abnormally");
            }
            finally
            {
                try
                {
                    Input.Complete();
                    // If _requestAborted is set, the connection has already been closed.
                    if (Volatile.Read(ref _requestAborted) == 0)
                    {
                        await TryProduceInvalidRequestResponse();
                        LifetimeControl.End(ProduceEndType.SocketShutdown);
                    }
                }
                catch (Exception ex)
                {
                    Log.LogWarning(0, ex, "Connection shutdown abnormally");
                }
            }
        }
    }
}
