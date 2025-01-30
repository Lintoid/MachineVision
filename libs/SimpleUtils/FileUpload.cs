using System;
using System.Net;  // For Dns.GetHostEntry(), etc.
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;  // for GZIP (GZipStream class)
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using System.Drawing;  // Image
using System.Drawing.Imaging;  // ImageFormat


// See instructions on including this :  http://stackoverflow.com/questions/7000811/cannot-find-javascriptserializer-in-net-4-0 
// To add a ref on an Assembly in VS :  Project->Add Reference->Assemblies->Framework and set CHECKBOX for System.Web.Extensions ...
using System.Web.Script.Serialization;    // for JavaScriptSerializer


namespace SimpleUtils
{
    public class FileUpload
    {
        public static int VerboseLevel = 0;


        /// <summary>
        /// Process an HTTP POST FILE UPLOAD request with image files, and extract the image data.
        /// </summary>
        /// <param name="request">HTTP POST request, with MULTIPART image data in payload</param>
        /// <param name="httpPostBody">if provided, use this POST BODY content; do NOT read it again from the request, because that read-once InputStream is consumed</param>
        /// <param name="errorStrOut"></param>
        /// <returns>map of original file names (with extensions indicating type, e.g. ".jpg") to Image files</returns>
        public static Dictionary<string, Image> GetImagesFromMultipartUploadRequest(HttpListenerRequest request, string httpPostBody, out string errorStrOut)
        {
            Dictionary<string, Image> fileNameToImageMap = new Dictionary<string, Image>();

            DebugPrint(1, "> GetImagesFromMultipartUploadRequest ");

            errorStrOut = "";

            try
            {
                //
                // There should be a Content-type header like :
                //     Content-type: multipart/form-data; boundary=----WebKitFormBoundaryvW1JjBAmu08hAY5Q
                // The 'boundary' string separates each uploaded image file's data.
                // Note : httpHeadersMap keys are lower-case header names, for consistency.
                //
                Dictionary<string, string> httpHeadersMap = SimpleUtils.WebServer.ParseHTTPRequestHeaders(request);
                string contentTypeHdr = httpHeadersMap.ContainsKey("content-type") ? httpHeadersMap["content-type"] : null;
                if (contentTypeHdr != null)
                {
                    //
                    // Verify that the Content-Type is MULTIPART; and extract the 'boundary' string, which separates each file of image data.
                    //
                    string[] parts = contentTypeHdr.Split(';');
                    if ((parts.Length >= 2) && (parts[0].Trim().ToLower() == "multipart/form-data"))
                    {
                        string[] parts2 = parts[1].Trim().Split('=');  // split e.g.  "boundary=----WebKitFormBoundaryvW1JjBAmu08hAY5Q"
                        if ((parts2.Length == 2) && (parts2[0].Trim().ToLower() == "boundary"))
                        {
                            string multipart_boundary_str = parts2[1];

                            //
                            // The ACTUAL BOUNDARY between sections is:    "--" + multipart_boundary_str + CRLF
                            // And the FINAL section ends with:            "--" + multipart_boundary_str + "--" + CRLF
                            //
                            const string boundary_delimiter = "--";

                            if (request.HttpMethod.ToUpper() == "POST")
                            {
                                // if httpPostBody is provided, use this POST BODY content; do NOT read it again from the request, because that read-once InputStream is consumed
                                string postBody = ((httpPostBody != null) && (httpPostBody.Length > 0)) ? httpPostBody : WebServer.ReadHTTPRequestPOSTBody(request);

                                // Split the entire message on the boundary + CRLF, to separate the sections.
                                string[] msg_sections = postBody.Split(new string[] { boundary_delimiter + multipart_boundary_str + "\r\n" }, StringSplitOptions.None);

                                //
                                // Go through each multipart section.  Each one is like a little HTTP request, with its own HEADERS.
                                //
                                foreach (string section_body in msg_sections)
                                {
                                    //
                                    // The FINAL section of the multipart msg ends with a slightly different delimiter (see above),
                                    // resulting in the boundary remaining at the end.  So remove it manually here.
                                    // (That final section should end in a CRLF; but handle it with or without CRLF in case of a bad browser implementation.)
                                    //
                                    string finalDelimWithoutCRLF = boundary_delimiter + multipart_boundary_str + boundary_delimiter;
                                    string finalDelimWithCRLF = finalDelimWithoutCRLF + "\r\n";
                                    string cleaned_section_body;
                                    if (section_body.EndsWith(finalDelimWithCRLF))
                                    {
                                        cleaned_section_body = section_body.Substring(0, section_body.Length - finalDelimWithCRLF.Length);
                                    }
                                    else if (section_body.EndsWith(finalDelimWithoutCRLF))
                                    {
                                        cleaned_section_body = section_body.Substring(0, section_body.Length - finalDelimWithoutCRLF.Length);
                                    }
                                    else
                                    {
                                        // this must not be the final section_body in msg_sections, ok
                                        cleaned_section_body = section_body;
                                    }

                                    //
                                    // Now go through this single multipart section.  Each one has some HTTP headers, each ending with CRLF.
                                    // There is then a double CRLF, followed by the actual image data.
                                    //
                                    string[] section_parts = cleaned_section_body.Split(new string[] { "\r\n" }, StringSplitOptions.None);

                                    string imgTypeFileExt = null;
                                    string imageFileName = null;
                                    for (int i = 0; i < section_parts.Length; i++)
                                    {
                                        string section_part = section_parts[i];

                                        if (section_part.Length > 0)
                                        {
                                            //
                                            // This is one of the section HEADERS.
                                            // Look for a Content-Type header like :
                                            //     Content-Type: image/jpeg
                                            //     Content-Type: image/bmp
                                            //     Content-Type: image/gif
                                            //     Content-Type: image/png
                                            //     Content-Type: image/tiff
                                            //
                                            // There should also be a Content-Disposition header, with the IMAGE FILE NAME; e.g. :
                                            //     Content-Disposition: form-data; name="user_image"; filename="onepixel_black.jpg"
                                            //
                                            string[] hdrParts = section_part.Split(':');
                                            if (hdrParts.Length == 2)
                                            {
                                                string hdrName = hdrParts[0].Trim().ToLower();
                                                string hdrVal = hdrParts[1].Trim().ToLower();
                                                if (hdrName == "content-type")
                                                {
                                                    switch (hdrVal)
                                                    {
                                                        case "image/jpeg": imgTypeFileExt = "jpg"; break;    // note : file ext is 'jpg', not 'jpeg'
                                                        case "image/bmp": imgTypeFileExt = "bmp"; break;
                                                        case "image/gif": imgTypeFileExt = "gif"; break;
                                                        case "image/png": imgTypeFileExt = "png"; break;

                                                        // browsers don't handle TIFF format; // TODO(ervin): convert to JPEG and save as such
                                                        // case "image/tiff": imgTypeFileExt = "tiff"; break;
                                                    }
                                                }
                                                else if (hdrName == "content-disposition")
                                                {
                                                    string[] dispParts = hdrVal.Split(';');
                                                    foreach (string dispPart in dispParts)
                                                    {
                                                        string[] dispKeyVal = dispPart.Split('=');  // split e.g. filename="onepixel_black.jpg"
                                                        if ((dispKeyVal.Length == 2) && (dispKeyVal[0].Trim().ToLower() == "filename"))
                                                        {
                                                            imageFileName = dispKeyVal[1].Replace("\"", "").Trim();
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                        else
                                        {
                                            //
                                            // This marks the spot between the double-CRLF that ends the HTTP headers.
                                            // If we saw a Content-Type header like "Content-Type: image/jpeg", then return the rest of the section as the [JPEG or other type] image.
                                            //
                                            if ((imgTypeFileExt != null) && (imageFileName != null) && (imageFileName.Length > 0) && (i + 1 < section_parts.Length))
                                            {
                                                //
                                                // section_parts[i+1] should have the actual IMAGE DATA.
                                                // NOTE that it will NOT be the FINAL section, because there is a final CRLF at the end of the data.
                                                //
                                                // In case there happen to be bytes matching CRLF in the image data,
                                                // JOIN any following sections resulting from the split on CRLF (that produced section_parts[] ).
                                                //
                                                string[] img_data_parts = section_parts.Skip(i + 1).Take(section_parts.Length - (i + 1)).ToArray();
                                                if (img_data_parts.Last().Length == 0)
                                                {
                                                    // get rid of last empty section, created by split on final CRLF
                                                    img_data_parts = img_data_parts.Take(img_data_parts.Length - 1).ToArray();
                                                }

                                                //
                                                // Usually img_data_parts[] will have length 1.
                                                // In case there were CRLF characters in the actual image data (rare), those are actually PART OF THE IMAGE,
                                                // so we need to put them back.
                                                //
                                                string image_data = (img_data_parts.Length == 1) ?
                                                                    img_data_parts[0] :
                                                                    String.Join("\r\n", img_data_parts);  // put back CRLF characters that are actually part of the image data

                                                //
                                                // Convert image_data (string) to a BYTE array.
                                                // NOTE : Encoding.ASCII.GetBytes(image_data)  DOES NOT WORK ; it converts high bytes to 0x3F.
                                                // We will do this MANUALLY.
                                                //
                                                byte[] imgBytes = new byte[image_data.Length];
                                                for (int b = 0; b < image_data.Length; b++)
                                                {
                                                    imgBytes[b] = (byte)image_data[b];
                                                }

                                                //
                                                // Generate an Image object from the image data.
                                                //   ; see : https://stackoverflow.com/questions/9173904/byte-array-to-image-conversion
                                                //
                                                MemoryStream stream = new MemoryStream(imgBytes, 0, imgBytes.Length);
                                                stream.Write(imgBytes, 0, imgBytes.Length);
                                                stream.Position = 0;
                                                Image uploadedImage = Image.FromStream(stream, true, true);

                                                // return the filename:Image mapping
                                                fileNameToImageMap[imageFileName] = uploadedImage;
                                            }

                                            // Done processing parts of this single multipart section
                                            break;
                                        }
                                    }
                                }

                            }
                            else
                            {
                                errorStrOut = "image upload request is not HTTP POST";
                            }
                        }
                        else
                        {
                            errorStrOut = "no boundary string found in image upload multipart HTTP POST request";
                        }

                    }
                    else
                    {
                        errorStrOut = "image upload request Content-Type is not multipart";
                    }
                }
                else
                {
                    errorStrOut = "image upload HTTP POST missing Content-Type header";
                }

            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                fileNameToImageMap.Clear();
            }

            DebugPrint(1, "< GetImagesFromMultipartUploadRequest - returning {0} images ", fileNameToImageMap.Count);

            return fileNameToImageMap;
        }


        /// <summary>
        /// DebugPrint
        /// </summary>
        /// <param name="minVerboseLevel"></param>
        /// <param name="format"></param>
        /// <param name="varArgs"></param>
        private static void DebugPrint(int minVerboseLevel, string format, params dynamic[] varArgs)
        {
            if (VerboseLevel >= minVerboseLevel)
            {
                Console.WriteLine(format, varArgs);
            }
        }

    }
}
