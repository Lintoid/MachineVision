using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;  // for StackTrace


namespace SimpleUtils
{
    public class Diagnostics
    {

        /// <summary>
        /// Generate a clean STACK TRACE as a string, without raising an exception.
        /// On Release builds, this is a more complete stack trace than you'd get in the standard exception handler.
        /// </summary>
        public static string StackTrace()
        {
            string traceStr = "";

            try
            {
                // see : http://peterkellner.net/2009/12/21/how-to-get-a-stack-trace-from-c-without-throwing-exception/
                var stackTrace = new StackTrace(true);  // must pass true to get line numbers

                traceStr = "STACK TRACE : ";
                traceStr += "\r\n ----------------------------------------";
                int stackLevel = 0;
                foreach (StackFrame r in stackTrace.GetFrames())
                {
                    stackLevel++;
                    if ((stackLevel == 1) && (r.GetMethod().ToString().IndexOf("StackTrace") > 0))
                    {
                        // don't show the StackTrace() call itself
                        continue;
                    }
                    traceStr += String.Format("\r\n    Filename: {0} Method: {1} Line: {2} Column: {3}  ", r.GetFileName(), r.GetMethod(), r.GetFileLineNumber(), r.GetFileColumnNumber());
                }
                traceStr += "\r\n ----------------------------------------\r\n";
            }
            catch (Exception e)
            {
                // fail silently
                traceStr += "<< StackTrace() raised EXCEPTION >>";
            }

            return traceStr;
        }


        /// <summary>
        /// DumpException
        /// </summary>
        /// <param name="e"></param>
        public static void DumpException(Exception e)
        {
            // This is called within an exception handler of the calling function; prevent from raising another exception here.
            try
            {
                // Under memory-constained conditions, the Exception object given to the caller's exception handler may be null.
                if (e != null)
                {
                    Console.WriteLine("\n EXCEPTION:\r\n    > SOURCE: {0}\r\n    > MESSAGE: {1}\r\n    > TRACE: {2}\r\n   > ToString: {3}\n\n", e.Source, e.Message, e.StackTrace, e.ToString());
                }
                else
                {
                    Console.WriteLine("<< Exception object passed to DumpException is null !! >>");
                }

                // This produces a more complete STACK TRACE; especially on Release builds.
                Console.WriteLine(StackTrace());
            }
            catch (Exception e2)
            {
                // I've actually seen THIS also re-raise an exception (perhaps due to memory stress). So do nothing, to avoid an unhandled exception in the caller.
                // Console.WriteLine("<< DumpException raised another exception !! >>");
            }
        }
    }
}
