using System;
using System.Net;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;  // for GZIP (GZipStream class)
using System.Text;
using System.Linq;


// See instructions on including this :  http://stackoverflow.com/questions/7000811/cannot-find-javascriptserializer-in-net-4-0 
// To add a ref on an Assembly in VS :  Project->Add Reference->Assemblies->Framework and set CHECKBOX for System.Web.Extensions ...
using System.Web.Script.Serialization;    // for JavaScriptSerializer

using System.Reflection;   // includes FieldInfo, BindingFlags, etc required for REFLECTION (e.g. enumerating fields/values of an unknown-type struct)


namespace SimpleUtils
{
    public class DataUtils
    {
        private static JavaScriptSerializer JsonSerializer = new JavaScriptSerializer();


        /// <summary>
        /// Parse command-line flags from command-line argument vector.
        /// This is a static function because the HTTP port # for the WebServer is a likely command-line parameter;
        /// and is not available until this function retrieves it.
        /// </summary>
        /// <param name="args"></param>
        /// <param name="requiredKeys"></param>
        /// <param name="optionalKeys"></param>
        /// <returns>dictionary with parameter-value mappings; or null on unexpected input</returns>
        public static Dictionary<string, string> ParseCommandLineFlags(string[] args, string[] requiredKeys, string[] optionalKeys)
        {
            Dictionary<string, string> paramsMap = new Dictionary<string, string>();

            //
            // Convert all keys to LOWER-CASE, for consistency.
            //
            for (int i = 0; i < requiredKeys.Length; i++)
            {
                requiredKeys[i] = requiredKeys[i].ToLower();
            }
            for (int i = 0; i < optionalKeys.Length; i++)
            {
                optionalKeys[i] = optionalKeys[i].ToLower();
            }

            List<string> unseenRequiredKeysList = new List<string>(requiredKeys.ToList());

            //
            // Any additional command-line arguments are optional, and must have the form:  key=value
            //
            for (int i = 0; i < args.Length; i++)
            {
                string argKeyVal = args[i];
                string[] parts = argKeyVal.Split('=');
                if (parts.Length >= 2)  // (>=, because value is allowed to include '=' chars)
                {
                    string argKey = parts[0].ToLower();  // 'key' part of 'key=value'
                    // string argValue = parts[1];   // 'value' part of 'key=value'
                    string argValue = argKeyVal.Substring(argKey.Length + 1);  // '=' chars within the value are allowed, hence take entire argKeyVal string after 'key='

                    //
                    // Handle user input forms like '--key=val'  or  '-key=val' .
                    //
                    if (argKey.StartsWith("--"))
                    {
                        argKey = argKey.Substring(2);
                    }
                    else if (argKey.StartsWith("-"))
                    {
                        argKey = argKey.Substring(1);
                    }

                    if ((argKey.Length > 0) && (argValue.Length > 0))
                    {
                        if (requiredKeys.Contains(argKey))
                        {
                            paramsMap[argKey] = argValue;

                            unseenRequiredKeysList.Remove(argKey);
                        }
                        else if (optionalKeys.Contains(argKey))
                        {
                            paramsMap[argKey] = argValue;
                        }
                        else
                        {
                            Console.WriteLine(" ERROR: undefined parameter '{0}' . ", argKey);
                            paramsMap = null;
                            break;
                        }
                    }
                    else
                    {
                        Console.WriteLine(" ERROR: bad parameter '{0}' . ", argKeyVal);
                        paramsMap = null;
                        break;
                    }
                }
                else
                {
                    Console.WriteLine(" ERROR: bad parameter '{0}' . ", argKeyVal);
                    paramsMap = null;
                    break;
                }
            }

            //
            // Make sure user entered all REQUIRED arguments.
            //
            if ((paramsMap != null) && (unseenRequiredKeysList.Count > 0))
            {
                string firstMissingKey = unseenRequiredKeysList[0];
                Console.WriteLine(" ERROR: {0} parameter is required. ", firstMissingKey);
                paramsMap = null;
            }

            return paramsMap;
        }


        /// <summary>
        /// Apply GZIP compression to data bytes.
        /// </summary>
        /// <param name="dataBytes"></param>
        /// <returns>GZIPPED bytes</returns>
        public static byte[] DoGZIPCompression(byte[] dataBytes)
        {
            byte[] gzippedBytes = null;

            // 
            // NOTE:  There is lots of incorrect documentation re how to perform GZIP in C#.
            //        See:  http://www.dotnetperls.com/compress for a correct sample.
            //
            using (MemoryStream memoryStream = new MemoryStream())
            {
                using (GZipStream gzipStream = new GZipStream(memoryStream, CompressionMode.Compress, true))
                {
                    gzipStream.Write(dataBytes, 0, dataBytes.Length);
                }
                gzippedBytes = memoryStream.ToArray();
            }

            return gzippedBytes;
        }


        /// <summary>
        /// Apply GZIP decompression to gzipped data bytes.
        /// </summary>
        /// <param name="gzippedBytes"></param>
        /// <returns>unzipped data bytes</returns>
        public static byte[] DoGZIPDecompression(byte[] gzippedBytes)
        {
            byte[] restoredDataBytes = null;

            // 
            // NOTE:  There is lots of incorrect documentation re how to perform GZIP in C#.
            //        See:  https://www.dotnetperls.com/decompress for a correct sample.
            //
            using (GZipStream unzipStream = new GZipStream(new MemoryStream(gzippedBytes), CompressionMode.Decompress, true))
            {
                const int chunkSize = 0x100000;  // 1MB
                byte[] buffer = new byte[chunkSize];
                using (MemoryStream memoryStream = new MemoryStream())
                {
                    int count = 0;
                    do
                    {
                        count = unzipStream.Read(buffer, 0, chunkSize);
                        if (count > 0)
                        {
                            memoryStream.Write(buffer, 0, count);
                        }
                    }
                    while (count > 0);

                    restoredDataBytes = memoryStream.ToArray();
                }
            }

            return restoredDataBytes;
        }


        /// <summary>
        /// SafeParseInt - recovers gracefully from non-numeric strings passed to int.Parse(), (and dumps warning, unlike int.TryParse() ) .
        /// </summary>
        /// <param name="numStr"></param>
        /// <param name="defaultValue"></param>
        public static int SafeParseInt(string numStr, int defaultValue)
        {
            int result;
            try
            {
                result = int.Parse(numStr);
            }
            catch
            {
                Console.WriteLine("(attempt to parse non-integer string '{0}' ; defaulting to {1}) ", numStr, defaultValue);
                result = defaultValue;
            }
            return result;
        }


        /// <summary>
        /// SafeParseLong - recovers gracefully from non-numeric strings passed to long.Parse(), (and dumps warning, unlike long.TryParse() ) .
        /// </summary>
        /// <param name="numStr"></param>
        /// <param name="defaultValue"></param>
        public static long SafeParseLong(string numStr, long defaultValue)
        {
            long result;
            try
            {
                result = long.Parse(numStr);
            }
            catch
            {
                Console.WriteLine("(attempt to parse non-integer string '{0}' ; defaulting to {1}) ", numStr, defaultValue);
                result = defaultValue;
            }
            return result;
        }


        /// <summary>
        /// SafeParseDouble - recovers gracefully from non-numeric strings passed to double.Parse(), (and dumps warning, unlike double.TryParse() ) .
        /// </summary>
        /// <param name="numStr"></param>
        /// <param name="defaultValue"></param>
        public static double SafeParseDouble(string numStr, double defaultValue)
        {
            double result;
            try
            {
                result = double.Parse(numStr);
            }
            catch
            {
                Console.WriteLine("(attempt to parse non-float string '{0}' ; defaulting to {1}) ", numStr, defaultValue);
                result = defaultValue;
            }
            return result;
        }


        /// <summary>
        /// SafeSubstring - wrapper for string.Substring() (which raises an exception if the index is out of range).
        /// </summary>
        /// <param name="str"></param>
        /// <param name="maxLen"></param>
        public static string SafeSubstring(string str, int maxLen)
        {
            return str.Substring(0, Math.Min(maxLen, str.Length));
        }


        /// <summary>
        /// SafeEncryptBase64
        /// </summary>
        /// <param name="clearTextStr"></param>
        public static string SafeEncryptBase64(string clearTextStr)
        {
            string encryptedStr = "";

            try
            {
                encryptedStr = Convert.ToBase64String(Encoding.ASCII.GetBytes(clearTextStr));
            }
            catch
            {
                Console.WriteLine("Exception attempting to base64 encrypt string '{0}' ", clearTextStr);
                encryptedStr = "";
            }
            return encryptedStr;
        }


        /// <summary>
        /// SafeDecryptBase64
        /// </summary>
        /// <param name="base64Str"></param>
        /// <param name="defaultValue"></param>   
        public static string SafeDecryptBase64(string base64Str, string defaultValue)
        {
            string decryptedStr = defaultValue;

            try
            {
                decryptedStr = System.Text.Encoding.Default.GetString(Convert.FromBase64String(base64Str));
            }
            catch
            {
                Console.WriteLine("(attempt to decrypt bad BASE64 string '{0}' ; defaulting to {1}) ", base64Str, defaultValue);
                decryptedStr = defaultValue;
            }
            return decryptedStr;
        }


        /// <summary>
        /// CreateDictionaryHierarchy - non-destructively walk the Dictionary, and add a hierarchical set of keys (if they don't already exist).
        /// </summary>
        /// <param name="str"></param>
        /// <param name="maxLen"></param>        
        public static void CreateDictionaryHierarchy(ref Dictionary<string, dynamic> dict, string[] keysToAdd)
        {
            Dictionary<string, dynamic> subDict = dict;  // subDict is a reference, beginning at the top-level of dict

            foreach (string key in keysToAdd)
            {
                if (!subDict.ContainsKey(key))
                {
                    // The dictionary does NOT have this key at this level; ADD the KEY.
                    subDict[key] = new Dictionary<string, dynamic>();
                }

                // now STEP DOWN into the sub-dictionary (whether we created it or not), to create the next sub-key ...
                subDict = subDict[key];
            }
        }
         

        /// <summary>
        /// Recursively deep-copy a dictionary of type Dictionary<string, dynamic>, without creating internal references.
        /// One SIDE EFFECT of this copy is that the output dict will SERIALIZE to JSON CONSISTENTLY
        /// (as it always generates the output dict's keys in the SAME ORDER; and JavaScriptSerializer.Serialize() processed them in creation order).
        /// </summary>
        /// <param name="origDict"></param>
        /// <returns>copy of the dictionary</returns>
        public static Dictionary<string, dynamic> DeepCopyDict(Dictionary<string, dynamic> origDict)
        {
            Dictionary<string, dynamic> dictCopy = new Dictionary<string, dynamic>();

            //
            // One SIDE EFFECT of this copy is that we always create the copy dict CONSISTENTLY,
            // creating the KEYS in the SAME ORDER.
            // This causes the output to always SERIALIZE to JSON CONSISTENTLY
            // (JavaScriptSerializer.Serialize() processes values in their creation order in the dictionary).
            //
            string[] sortedKeys = (new List<string>(origDict.Keys)).ToArray();
            if (sortedKeys.Length > 0)
            {
                Array.Sort(sortedKeys);
            }

            foreach (string key in sortedKeys)
            {
                dynamic value = origDict[key];

                Type valueType = value.GetType();
                if (valueType.Equals(typeof(Dictionary<string, dynamic>)))
                {
                    // Value is a nested DICTIONARY.  Call ourselves RECURSIVELY to DEEP-COPY it.
                    dictCopy[key] = DeepCopyDict(value);
                }
                else
                {
                    // simple type; just copy it into the new dict
                    dictCopy[key] = value;
                }
            }

            return dictCopy;
        }


        /// <summary>
        /// JSON-ize the input dictionary, but with CONSISTENT key order (so that equivalent dictionaries always serialize identically).
        /// At each level, keys will be in SORTED order.  The result can be used as a HASH identifier for the key-value combination in the dictionary.
        /// This is also a SAFE Serialize() call, because of the try/catch.
        /// </summary>
        /// <param name="dict"></param>
        /// <returns>unique JSON serialization string</returns>
        public static string DictToUniqueJSONStr(Dictionary<string, dynamic> dict)
        {
            string dictUniqueJsonStr = null;

            try
            {
                //
                // Create a HASH STRING to act as a unique identifying key for the key-values mapping.
                // NOTE: JavaScriptSerializer does NOT serialize a dict CONSISTENTLY;
                //       it walks keys in creation order; DeepCopyDict() re-creates keys in consistent order, so that the JSON string will be consistent.
                //
                dictUniqueJsonStr = JsonSerializer.Serialize((object)DeepCopyDict(dict));
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                dictUniqueJsonStr = null;
            }

            return dictUniqueJsonStr;
        }


        /// <summary>
        /// This utility uses C# REFLECTION to turn a simple 'flat' struct (like: struct ? { string name; int age; } ) into a Dictionary of key-values,
        /// which can then be JSON-ized.
        /// </summary>
        /// <param name="myStruct">flat structure of ANY TYPE</param>
        /// <returns>Dictionary of key-value pairs from struct fields</returns>
        public static Dictionary<string, dynamic> StructToDict(object myStruct)
        {
            Dictionary<string, dynamic> structKeyValsMap = new Dictionary<string, dynamic>();

            FieldInfo[] allFieldsInfo = myStruct.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (FieldInfo info in allFieldsInfo)
            {
                string fieldName = info.Name;
                dynamic value = info.GetValue(myStruct);
                structKeyValsMap[fieldName] = value;
            }

            return structKeyValsMap;
        }


        /// <summary>
        /// This utility uses C# REFLECTION to turn a simple 'flat' struct (like: struct ? { string name; int age; } ) into a Dictionary of key-values,
        /// and then into a JSON string (which can be returned to a Javascript client and eval()'d to a Javascript associative array).
        /// </summary>
        /// <param name="myStruct">flat structure of ANY TYPE</param>
        /// <returns>JSON string with key-value pairs from struct fields</returns>
        public static string StructToJSON(object myStruct)
        {
            Dictionary<string, dynamic> structKeyValsMap = StructToDict(myStruct);

            // convert some value types to make them JSON-izable
            List<string> keys = new List<string>(structKeyValsMap.Keys);
            foreach (string key in keys)
            {
                Type valueType = structKeyValsMap[key].GetType();

                // convert DateTime to string
                if (valueType.Equals(typeof(DateTime)))
                {
                    structKeyValsMap[key] = structKeyValsMap[key].ToString();
                }
            }

            string jsonStr = JsonSerializer.Serialize(structKeyValsMap);
            return jsonStr;
        }


        /// <summary>
        /// Initialize a simple log-table STRUCT from an array of STRINGs.
        /// NOTE:  ONLY HANDLES a flat struct with BASIC field TYPES like:  string, int, long, double .
        /// </summary>
        /// <param name="myStruct">flat structure of ANY TYPE, with ONLY BASIC TYPES: string, int, long, double </param>
        /// <param name="fieldsArray">array of strings representing field values, (as we get from a StructuredStream log in Cosmos)</param>
        /// <param name="conversionErrorStr">on failure, conversionErrorStr returns a detailed error string</param>
        /// <returns>boolean indicating conversion success/failure</returns>
        public static bool StructFromFieldArray(ref object myStruct, string[] fieldsArray, out string conversionErrorStr)
        {
            bool didExtract = true;
            conversionErrorStr = null;

            FieldInfo[] allFieldsInfo = myStruct.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fieldsArray.Length != allFieldsInfo.Length)
            {
                conversionErrorStr = String.Format("StructFromFieldArray: wrong number of fields ({0} vs {1})", fieldsArray.Length, allFieldsInfo.Length);
                didExtract = false;
            }
            else
            {
                int fieldIndex = 0;
                foreach (FieldInfo info in allFieldsInfo)
                {
                    string fieldName = info.Name;

                    string fieldValStr = fieldsArray[fieldIndex];

                    //
                    // Get the TYPE of the struct field.
                    // NOTE:  FieldInfo.FieldType gives the actual type of the struct's field that we want.
                    //        FieldInfo.GetType() returns the type of the FieldInfo itself (System.Reflection.RtFieldInfo), not the type of the represented field.
                    //        FieldInfo.ReflectedType gives the entire struct's type.
                    //        FieldInfo.DeclaringType gives the container object's type.
                    //
                    System.Type fieldType = info.FieldType;
                    if (fieldType.Equals(typeof(System.String)) || fieldType.Equals(typeof(string)))
                    {
                        info.SetValue(myStruct, fieldValStr);
                    }
                    else if (fieldType.Equals(typeof(System.Int32)) || fieldType.Equals(typeof(int)))
                    {
                        try
                        {
                            info.SetValue(myStruct, int.Parse(fieldValStr));
                        }
                        catch
                        {
                            conversionErrorStr = String.Format("StructFromFieldArray: FAILED to convert field '{0}' (index {1}) string value '{2}' to INT . ", fieldName, fieldIndex, fieldValStr);
                            didExtract = false;
                        }
                    }
                    else if (fieldType.Equals(typeof(System.Int64)) || fieldType.Equals(typeof(long)))
                    {
                        try
                        {
                            info.SetValue(myStruct, long.Parse(fieldValStr));
                        }
                        catch
                        {
                            conversionErrorStr = String.Format("StructFromFieldArray: FAILED to convert field '{0}' (index {1}) string value '{2}' to LONG . ", fieldName, fieldIndex, fieldValStr);
                            didExtract = false;
                        }
                    }
                    else if (fieldType.Equals(typeof(System.Double)) || fieldType.Equals(typeof(double)))
                    {
                        try
                        {
                            info.SetValue(myStruct, double.Parse(fieldValStr));
                        }
                        catch
                        {
                            conversionErrorStr = String.Format("StructFromFieldArray: FAILED to convert field '{0}' (index {1}) string value '{2}' to DOUBLE . ", fieldName, fieldIndex, fieldValStr);
                            didExtract = false;
                        }
                    }
                    else
                    {
                        conversionErrorStr = String.Format("StructFromFieldArray: FAILED to convert field '{0}' (index {1}) string value '{2}' to UNHANDLED TYPE '{3}' . ", fieldName, fieldIndex, fieldValStr, fieldType.ToString());
                        didExtract = false;
                    }

                    if (!didExtract)
                    {
                        break;
                    }

                    fieldIndex++;
                }

            }

            return didExtract;
        }


        /// <summary>
        /// Parse an ENUM value from string to the enum type.
        /// usage :  SafeParseEnum(typeof(MyEnumType), valueStr, myDefaultType);
        /// </summary>
        /// <param name="enumType"></param>
        /// <param name="valStr"></param>
        /// <param name="defaultEnumVal"></param>
        /// <returns>parsed enum type</returns>
        public static object SafeParseEnum(Type enumType, string valStr, object defaultEnumVal)
        {
            object parsedEnumVal;

            try
            {
                parsedEnumVal = Enum.Parse(enumType, valStr, false);
            }
            catch (Exception e)
            {
                Diagnostics.DumpException(e);
                parsedEnumVal = defaultEnumVal;
            }

            return parsedEnumVal;
        }

    }
}
