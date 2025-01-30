using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using System.Drawing;
using System.Drawing.Drawing2D;  // to add reference : right-click project -> Add -> Reference -> Assemblies ... and select System.Drawing
using System.Drawing.Imaging;

//
// Microsoft.ML.Transforms
// In Visual Studio -> Tools -> Nuget Package Manager -> Package Manager Console, run :
//     Install-Package Microsoft.ML
//     Install-Package Microsoft.ML.ImageAnalytics
//     Install-Package Microsoft.ML.TimeSeries
//     Install-Package Microsoft.ML.TensorFlow
//     Install-Package Microsoft.ML.Vision
//
// You MUST ALSO install this package :
//     Install-Package SciSharp.TensorFlow.Redist -version 2.3.1
//     (or you will get this ERROR at runtime : System.DllNotFoundException: Unable to load DLL 'tensorflow' ).
//     NOTE : With 2.16.0 of SciSharp.TensorFlow.Redist, I later get this OTHER ERROR during training :
//            "Unable to find an entry point named 'TF_StringEncodedSize' in DLL 'tensorflow'."
//            Use earlier version (2.3.1) ? per : https://github.com/dotnet/machinelearning-samples/issues/880
//
// https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.transforms?view=ml-dotnet
// https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.transforms.image?view=ml-dotnet
//
using Microsoft.ML;
using Microsoft.ML.Transforms;
using Microsoft.ML.Transforms.Image;
using Microsoft.ML.Transforms.TimeSeries;
using Microsoft.ML.Transforms.Text;
using Microsoft.ML.Transforms.Onnx;
using Microsoft.ML.Data;
using Microsoft.ML.TensorFlow;
using Microsoft.ML.Trainers;
using Microsoft.ML.Vision;
using static Microsoft.ML.DataOperationsCatalog;
// using Microsoft.ML.ImageAnalytics;  // not necessary


//
// OnnxRuntime
// In Visual Studio -> Tools -> Nuget Package Manager -> Package Manager Console, run :
//     Install-Package Microsoft.ML.OnnxRuntime
//     Install-Package Microsoft.ML.OnnxTransformer
//     Install-Package Microsoft.ML.OnnxConverter
//
// see : https://onnxruntime.ai/docs/get-started/with-csharp.html 
//
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
// using Microsoft.ML.OnnxTransformer;  // no
using Microsoft.ML.Transforms.Onnx;  //OnnxOptions, OnnxScoringEstimator, OnnxTransformer 


//
// System.Numerics.Tensors
// In Visual Studio -> Tools -> Nuget Package Manager -> Package Manager Console, run :
//     Install-Package System.Numerics.Tensors
//
using System.Numerics.Tensors;

using Tensorflow.Operations.Activation;

// our libs
using SimpleUtils;



namespace MachineVision
{

    /// <summary>
    /// OnnxObjectDetection - Machine Vision library for doing YOLO (you only look once) object detection within larger images,
    ///                       using a provided (pre-trained) ONNX model.
    ///                       Such models (e.g. TinyYoloV2) are available publicly on GitHub and the ONNX Model Zoo.
    /// </summary>
    public class OnnxObjectDetection
    {
        // BoundingBox identifies a single identified object in a single image
        public struct BoundingBox
        {
            public Rectangle Dimensions { get; set; }
            public string Label { get; set; }  // recognized object class name
            public float Confidence { get; set; }
            public float[] ObjectClassScores { get; set; }  // this gets a probability distribution (SoftMax) of object class scores (which sums to 1.0)
        }

        //
        // DEFAULT params for the YOLO engine are set to match the public TinyYoloV2 model :
        //     https://github.com/onnx/models/tree/main/validated/vision/object_detection_segmentation/tiny-yolov2 
        // ... as described in this tutorial : https://learn.microsoft.com/en-us/dotnet/machine-learning/tutorials/object-detection-onnx .
        // 
        // GRID CELLS are expained here : https://stats.stackexchange.com/questions/507090/what-are-grids-and-detection-at-different-scales-in-yolov3  
        //
        // We make these defaults PUBLIC so a client app can display them with optional parameters.
        //
        public const int DEFAULT_ROW_COUNT = 13;
        public const int DEFAULT_COLUMN_COUNT = 13;
        public const int DEFAULT_CHANNEL_COUNT = 125;
        public const int DEFAULT_BOXES_PER_CELL = 5;
        public const int DEFAULT_CLASS_COUNT = 20;
        public const int DEFAULT_CELL_WIDTH = 32;
        public const int DEFAULT_CELL_HEIGHT = 32;


        // the input and output nodes (tensors) in the TinyYoloV2 model are called "image" and "grid"
        public const string DEFAULT_MODEL_INPUT_TENSOR_NAME = "image";
        public const string DEFAULT_MODEL_OUTPUT_TENSOR_NAME = "grid";

        //
        // These YOLO parameters must be set to match the ONNX model file that is loaded by the client.
        // Defaults match the public TinyYoloV2 ONNX YOLO model.
        //
        // You need to set these values EXACTLY RIGHT for the loaded ONNX YOLO model.
        // Otherwise, you will get non-sensical results; because these settings determine
        // offsets in the model's output tensor for each piece of scoring and bounding-box info.
        //
        public class YoloModelSettings
        {
            public int row_count { get; set; } = DEFAULT_ROW_COUNT;
            public int column_count { get; set; } = DEFAULT_COLUMN_COUNT;
            public int channel_count { get; set; } = DEFAULT_CHANNEL_COUNT;
            public int boxes_per_cell { get; set; } = DEFAULT_BOXES_PER_CELL;
            public int class_count { get; set; } = DEFAULT_CLASS_COUNT;
            public int cell_width { get; set; } = DEFAULT_CELL_WIDTH;
            public int cell_height { get; set; } = DEFAULT_CELL_HEIGHT;


            public string model_input_tensor_name { get; set; } = DEFAULT_MODEL_INPUT_TENSOR_NAME;
            public string model_output_tensor_name { get; set; } = DEFAULT_MODEL_OUTPUT_TENSOR_NAME;
        }

        public static int VerboseLevel = 0;  // client can set this directly to enable tracing for all instances

        // Fixed constants for YOLO models.
        public const int YOLO_BOX_INFO_FEATURE_COUNT = 5;  // the features are : X, Y, WIDTH, HEIGHT, CONFIDENCE (logit) ; followed by per-class scores


        private YoloModelSettings _yoloSettings = new YoloModelSettings();
        private string _onnxModelFilePath = null;
        private ITransformer _yoloTransformer = null;  // gets the loaded ONNX YOLO model
        private List<InputImageData> _imagesToScan = null;
        private IDataView _imagesDataView = null;       // loaded test images, in which we will attempt to discover objects recognized by the ONNX YOLO model

        // https://learn.microsoft.com/en-us/dotnet/api/microsoft.ml.mlcontext?view=ml-dotnet 
        private Microsoft.ML.MLContext _mlContext = new Microsoft.ML.MLContext();

        // client can provide explicit label names, matching the provided YOLO model; otherwise, labels will be "0", "1", ...
        private string[] _object_class_labels = new string[] { };

        // BOUNDING-BOX colors for detected objects; for the default bounding-box drawing, we will ROTATE through these, if there are more object classes.
        private Color[] _objectClassColors = new Color[]
        {
                Color.Khaki,
                Color.Fuchsia,
                Color.Silver,
                Color.RoyalBlue,
                Color.Green,
                Color.DarkOrange,
                Color.Purple,
                Color.Gold,
                Color.Red,
                Color.Aquamarine,
                Color.Lime,
                Color.AliceBlue,
                Color.Sienna,
                Color.Orchid,
                Color.Tan,
                Color.LightPink,
                Color.Yellow,
                Color.HotPink,
                Color.OliveDrab,
                Color.SandyBrown
        };

        //
        // Anchors are predefined height and width ratios of bounding boxes.
        // These should match the ASPECT RATIOS of the object images used to train the YOLO model.
        //
        // The number of (width, height) pairs must be equal to YoloModelSettings.boxes_per_cell .
        //
        // https://www.mathworks.com/help/vision/ug/anchor-boxes-for-object-detection.html
        //
        // In the original tutorial, this is called 'anchors', and his hardcoded as a flat array of (width, height) pairs :
        // private float[] anchors = new float[] { 1.08F, 1.19F,  3.42F, 4.41F,  6.63F, 11.38F,  9.42F, 5.11F,  16.62F, 10.52F };
        //
        // If loading a YOLO model other than TinyYoloV2, you should update these aspect ratio via SetBoundingBoxAspectRatios(),
        // to the aspect ratios of object images used to train the model.
        //
        private float[,] _boundingBoxAspectRatios = { { 1.08f, 1.19f }, { 3.42f, 4.41f }, { 6.63f, 11.38f }, { 9.42f, 5.11f }, { 16.62f, 10.52f }, };



        // This defines a SCHEMA for how input IMAGE data is expected by the pipeline
        private class InputImageData
        {
            [LoadColumn(0)]
            public string ImagePath;

            [LoadColumn(1)]
            public string Label;  // this gets the image filename

            public static IEnumerable<InputImageData> ReadFromFile(string imageFolder)
            {
                return Directory
                    .GetFiles(imageFolder)
                    .Select(filePath => new InputImageData { ImagePath = filePath, Label = Path.GetFileName(filePath) });
            }
        }

        // pre-allocate these, so we don't re-create them for each image
        private static ImageCodecInfo MyImgCodecInfo = ImageCodecInfo.GetImageEncoders().FirstOrDefault(ie => ie.MimeType == "image/jpeg");
        private static EncoderParameters MyEncoderParams = new EncoderParameters(1);


        /// <summary>
        /// OnnxObjectDetection constructor
        /// </summary>
        /// <param name="onnxModelFilePath"></param>
        /// <param name="yoloSettings"></param>
        public OnnxObjectDetection(string onnxModelFilePath, YoloModelSettings yoloSettings) 
        {
            this._onnxModelFilePath = onnxModelFilePath;
            this._yoloSettings = yoloSettings;

            bool loadOk = this.LoadOnnxModel();
            if (!loadOk)
            {
                throw new Exception(String.Format("FAILED to load ONNX model file from '{0}' . ", onnxModelFilePath));
            }
        }


        /// <summary>
        /// LoadOnnxModel
        /// </summary>
        /// <returns></returns>
        private bool LoadOnnxModel()
        {
            bool ok = false;

            DebugPrint(1, " > LoadOnnxModel from file '{0}' ...  ", this._onnxModelFilePath);

            try
            {
                // the pipeline stages will need to agree on their successive output/input column names; so just pick one (it can be anything)
                string IN_OUT_COL_NAME = this._yoloSettings.model_input_tensor_name;  // "image";  // use the model's input tensor name as our inter-pipeline-layer name ??

                // The scanned images are resized to these dimensions. With default values, both width and height are : 32 * 13 == 416
                int image_width = this._yoloSettings.cell_width * this._yoloSettings.column_count;
                int image_height = this._yoloSettings.cell_height * this._yoloSettings.row_count;

                //
                // This pipeline will just load the ONNX MODEL from file (not any images).
                // This only defines the pipeline, with no execution.
                //
                // TODO : BUGBUG : determine correct ExtractPixels() params ; e.g.  interleavePixelColors:true, offsetImage:117   ?
                //
                IEstimator<ITransformer> pipeline =
                    this._mlContext.Transforms.LoadImages(outputColumnName: IN_OUT_COL_NAME, imageFolder: "", inputColumnName: nameof(InputImageData.ImagePath))
                    .Append(this._mlContext.Transforms.ResizeImages(
                                                            outputColumnName: IN_OUT_COL_NAME,
                                                            imageWidth: image_width,
                                                            imageHeight: image_height,
                                                            inputColumnName: IN_OUT_COL_NAME))
                    .Append(this._mlContext.Transforms.ExtractPixels(outputColumnName: IN_OUT_COL_NAME))
                    .Append(this._mlContext.Transforms.ApplyOnnxModel(
                                                            modelFile: this._onnxModelFilePath,
                                                            outputColumnNames: new[] { this._yoloSettings.model_output_tensor_name },
                                                            inputColumnNames: new[] {  this._yoloSettings.model_input_tensor_name }));

                //
                // NOTE : re inputColumnNames : despite matching the input tensor name with what is shown in https://netron.app/ 
                //        for the ONNX YOLO model, we often get this error : 
                //        Microsoft.ML.OnnxTransformer : Could not find input column 'images'
                //        SEE analysis of this problem here :
                //          https://stackoverflow.com/questions/57264865/cant-get-input-column-name-of-onnx-model-to-work
                //

                //
                // We are not loading any actual images yet.
                // However the pipeline needs to know the input data SCHEMA.
                // So we pass this EMPTY IDataView for the images, just to provide the schema.
                //
                IDataView emptyImgData = this._mlContext.Data.LoadFromEnumerable(new List<InputImageData>());

                // EXECUTE the PIPELINE to load the model (no images; no training or inference).
                this._yoloTransformer = pipeline.Fit(emptyImgData);

                ok = true;
            }
            catch (Exception e)
            {
                SimpleUtils.Diagnostics.DumpException(e);
                this._yoloTransformer = null;
                ok = false;
            }

            DebugPrint(1, " < LoadOnnxModel ok = {0} ", ok);

            return ok;
        }


        /// <summary>
        /// LoadImages - load the images to scan, in an attempt to find objects identified by the loaded ONNX YOLO model
        /// </summary>
        /// <param name="imagesDirectoryPath"></param>
        /// <returns></returns>
        public bool LoadImages(string imagesDirectoryPath)
        {
            bool ok = false;

            DebugPrint(1, " > LoadImages from directory '{0}' ...  ", imagesDirectoryPath);

            int numImagesLoaded = 0;
            try
            {
                this._imagesToScan = InputImageData.ReadFromFile(imagesDirectoryPath).ToList();
                numImagesLoaded = this._imagesToScan.Count;
                if (numImagesLoaded > 0)
                {
                    this._imagesDataView = this._mlContext.Data.LoadFromEnumerable(this._imagesToScan);
                    ok = true;
                }
                else 
                {
                    DebugPrint(0, "ERROR in LoadImages() : no images found in directory '{0}' ", imagesDirectoryPath);
                }
            }
            catch (Exception e)
            {
                SimpleUtils.Diagnostics.DumpException(e);
                numImagesLoaded = 0;
                ok = false;
            }

            DebugPrint(1, " < LoadImages ; ok = {0}, loaded {1} images  ", ok, numImagesLoaded);

            return ok;
        }


        /// <summary>
        /// FindBoundingBoxes
        /// </summary>
        /// <param name="imagesWithBoxesMapOut"></param>
        /// <param name="thresholdConfidence"></param>
        /// <param name="maxBoxesPerImage"></param>
        /// <param name="maxBoxesOverlap"></param>
        /// <returns></returns>
        public Dictionary<string, List<BoundingBox>> FindBoundingBoxes(
            out Dictionary<string, Image> imagesWithBoxesMapOut,
            float thresholdConfidence = 0.0f,
            int maxBoxesPerImage = 5,
            float maxBoxesOverlap = 0.5f)
        {
            Dictionary<string, List<BoundingBox>> boundingBoxesMap = null;  // will map image name (key) to list of bounding boxes

            DebugPrint(1, " > FindBoundingBoxes ");

            imagesWithBoxesMapOut = null;

            try 
            {
                if (this._yoloTransformer == null)
                {
                    DebugPrint(0, "ERROR in OnnxObjectDetection.FindBoundingBoxes() : ONNX YOLO model is not loaded");
                }
                else if (this._imagesDataView == null)
                {
                    DebugPrint(0, "ERROR in OnnxObjectDetection.FindBoundingBoxes() : image data is not loaded");
                }
                else if (this._boundingBoxAspectRatios.GetLength(0) != this._yoloSettings.boxes_per_cell)
                {
                    DebugPrint(0, "ERROR in OnnxObjectDetection.FindBoundingBoxes() : number of boudingBoxAspectRatios ({0}) is not equal to YoloModelSettings.boxes_per_cell ({1}) . ", this._boundingBoxAspectRatios.GetLength(0), this._yoloSettings.boxes_per_cell);
                }
                else
                {
                    // simply apply the Transformer PIPELINE to the loaded image data
                    IDataView scoredData = this._yoloTransformer.Transform(this._imagesDataView);

                    //
                    // For each image, we extract an array of scores per grid cell and per object class.
                    // This is part of the OUTPUT TENSOR of the model -- which WinML flattens.
                    // We'll extract the values to a usable form below.
                    //
                    IEnumerable<float[]> allImageScores = scoredData.GetColumn<float[]>(this._yoloSettings.model_output_tensor_name);

                    boundingBoxesMap = new Dictionary<string, List<BoundingBox>>();
                    imagesWithBoxesMapOut = new Dictionary<string, Image>();

                    //
                    // Pull out the BOUNDING BOX and CONFIDENCE data from the flattened output tensor, for each image.
                    //
                    // TODO : consider THREADING by image, for PERF
                    //
                    for (int imgIndex = 0; imgIndex < allImageScores.Count(); imgIndex++)
                    {
                        float[] singleImageScores = allImageScores.ElementAt(imgIndex);

                        string imgFilePath = this._imagesToScan.ElementAt(imgIndex).ImagePath;
                        string imgFileName = this._imagesToScan.ElementAt(imgIndex).Label;

                        DebugPrint(2, " (analyzing results for image '{0}') ... ", imgFileName);

                        // Create entries in the returned boundingBoxesMap for every image -- even if no objects are detected; index by image filename
                        boundingBoxesMap[imgFileName] = new List<BoundingBox>();

                        for (int row = 0; row < this._yoloSettings.row_count; row++)
                        {
                            for (int col = 0; col < this._yoloSettings.column_count; col++)
                            {
                                for (int boxIndexInCell = 0; boxIndexInCell < this._yoloSettings.boxes_per_cell; boxIndexInCell++)
                                {
                                    BoundingBox boundingBox = GetBoundingBoxDimensions(singleImageScores, row, col, boxIndexInCell);
                                    if (boundingBox.Confidence >= thresholdConfidence)
                                    {

                                        // BUGBUG FINISH - we should RE-SCALE the bounding box to the original image size !!

                                        boundingBoxesMap[imgFileName].Add(boundingBox);
                                    }

                                }
                            }
                        }

                        // FILTER to limit number of objects identified per image; and bounding-box overlap
                        boundingBoxesMap[imgFileName] = FilterBoundingBoxes(boundingBoxesMap[imgFileName], maxBoxesPerImage, maxBoxesOverlap);

                        //
                        // Re-load the original image from file, and DRAW the BOUNDING BOXES on it.
                        // Do this even if there are no bounding boxes.
                        //
                        Image origImg = Image.FromFile(imgFilePath);
                        if (origImg != null)
                        {
                            // The scanned images were resized to these dimensions. With default values, both width and height are : 32 * 13 == 416
                            int image_width = this._yoloSettings.cell_width * this._yoloSettings.column_count;
                            int image_height = this._yoloSettings.cell_height * this._yoloSettings.row_count;

                            DebugPrint(2, " (drawing {0} bounding boxes for image '{1}') ... ", boundingBoxesMap[imgFileName].Count, imgFileName);

                            //
                            // RESIZE the input image to our internal working size.
                            // TODO(ervin): BUGBUG - we should be RESIZING the returned image and bounding box to the ORIGINAL size .
                            //
                            Image imgWithBoxes = ResizeImage(origImg, image_width, image_height, createSavableImage:true);

                            foreach (BoundingBox boundingBox in boundingBoxesMap[imgFileName])
                            {
                                // get the top-scored object class for this bounding box
                                (int topClassIndex, float topClassScore) = boundingBox.ObjectClassScores
                                               .Select((classScore, index) => (Index: index, Value: classScore))
                                               .OrderByDescending(result => result.Value)  // sort by high-to-low classScore
                                               .First();

                                // for the bounding-box COLOR, if there are not enough colors set, ROTATE though the colors the available colors
                                System.Drawing.Color rectEdgeColor = (this._objectClassColors.Length > 0) ?
                                                                        this._objectClassColors[topClassIndex % this._objectClassColors.Length] :
                                                                        Color.Red;
                                int edgeWidthPixels = 2;
                                OverlayRectangle(ref imgWithBoxes,
                                                boundingBox.Dimensions.X, boundingBox.Dimensions.Y,
                                                boundingBox.Dimensions.Width, boundingBox.Dimensions.Height,
                                                rectEdgeColor, edgeWidthPixels);
                            }
                            imagesWithBoxesMapOut[imgFileName] = imgWithBoxes;
                        }

                    }
                }
            }
            catch (Exception e)
            {
                SimpleUtils.Diagnostics.DumpException(e);
                boundingBoxesMap = null;
                imagesWithBoxesMapOut = null;
            }

            DebugPrint(1, " < FindBoundingBoxes returning bounding-box info for {0} images ", (boundingBoxesMap != null) ? boundingBoxesMap.Count : 0);

            return boundingBoxesMap;
        }


        /// <summary>
        /// GetBoundingBoxDimensions
        /// </summary>
        /// <param name="singleImageScores"></param>
        /// <param name="row"></param>
        /// <param name="col"></param>
        /// <param name="boxIndexInCell"></param>
        /// <returns></returns>
        private BoundingBox GetBoundingBoxDimensions(float[] singleImageScores, int row, int col, int boxIndexInCell)
        {
            //
            // singleImageScores are the values from the model's flattened output tensor, for a single image.
            //
            // Conceptually, for each potential box in each cell, there is :
            //     X, Y, WIDTH, HEIGHT, confidence ... followed by a score per object class.
            //     Those first 5 (YOLO_BOX_INFO_FEATURE_COUNT) features are fixed by the YOLO spec.
            //
            // However, the values are packed by "channel", such that e.g. all X values are packed together.
            //
            // An output "channel" in those values has a group of values of one type for ALL CELLS.
            // The CHANNELS are ordered as :
            //     [0] all bounding-box X values
            //     [1] all bounding-box Y values
            //     [2] all WIDTHS
            //     [3] all HEIGHTS
            //     [4] all CONFIDENCE scores (as logits)
            // ... (the above are the 5 fixed features per YOLO spec, i.e. YOLO_BOX_INFO_FEATURE_COUNT == 5)
            // ... followed by a score per OBJECT CLASS (variable).
            //
            // There are (row_count * column_count) values per channel.
            //
            // The per CELL data is arranged in ROW-FIRST order.
            //
            int numValuesPerCellBox = YOLO_BOX_INFO_FEATURE_COUNT + this._yoloSettings.class_count;
            int channel = boxIndexInCell * numValuesPerCellBox;
            int numCells = this._yoloSettings.row_count * this._yoloSettings.column_count;  // this is called 'channelStride' in the tutorial sample

            //
            // TODO(ervin): BUGBUG -- I think the original tutorial sample had a BUG here.
            // They passed row for 'x' and col for 'y' .  It didn't matter because row_count == column_count in their sample.
            // VERIFY that this is correct for unequal row_count and column_count .
            //
            int offset_X = ((channel + 0) * numCells) + (row * this._yoloSettings.column_count) + col;
            int offset_Y = ((channel + 1) * numCells) + (row * this._yoloSettings.column_count) + col;
            int offset_Width = ((channel + 2) * numCells) + (row * this._yoloSettings.column_count) + col;
            int offset_Height = ((channel + 3) * numCells) + (row * this._yoloSettings.column_count) + col;
            int offset_Confidence = ((channel + 4) * numCells) + (row * this._yoloSettings.column_count) + col;

            //
            // This is the bounding box, with FLOATING POINT (not pixel x/y) values.
            // These values are LOGITS for bounding-box dimensions WITHIN a CELL.
            // They include NEGATIVE values (even for width and height) !!
            //
            RectangleF boxDimLogits = new RectangleF();
            boxDimLogits.X = singleImageScores[offset_X];
            boxDimLogits.Y = singleImageScores[offset_Y];
            boxDimLogits.Width = singleImageScores[offset_Width];
            boxDimLogits.Height = singleImageScores[offset_Height];

            DebugPrint(3, "raw bounding box from model out tensor : x = {0}, y = {1}, width = {2}, height = {3} ", boxDimLogits.X, boxDimLogits.Y, boxDimLogits.Width, boxDimLogits.Height);

            // Map the intra-cell bounding-box rectangle to CELL dimensions
            float cell_x = ((float)row + Sigmoid(boxDimLogits.X)) * this._yoloSettings.cell_width;
            float cell_y = ((float)col + Sigmoid(boxDimLogits.Y)) * this._yoloSettings.cell_height;
            float cell_width = (float)Math.Exp(boxDimLogits.Width) * this._yoloSettings.cell_width * this._boundingBoxAspectRatios[boxIndexInCell, 0];
            float cell_height = (float)Math.Exp(boxDimLogits.Height) * this._yoloSettings.cell_height * this._boundingBoxAspectRatios[boxIndexInCell, 1];

            DebugPrint(3, "cell-mapped bounding box : x = {0}, y = {1}, width = {2}, height = {3} ", cell_x, cell_y, cell_width, cell_height);

            // cell_x and cell_y computed above are at the centers of the intended boxes; shift them to the upper-left corner
            cell_x -= cell_width / 2.0f;
            cell_y -= cell_height / 2.0f;

            Rectangle boundingBoxDims = new Rectangle((int)Math.Round(cell_x), (int)Math.Round(cell_y), (int)Math.Round(cell_width), (int)Math.Round(cell_height));


            //
            // The confidence score is a LOGIT value (in range[-inf, +inf]) ; convert it to a probability (in range[0, 1]) .
            // This is the confidence for the bounding box, not the final labeling.
            //
            float boxConfidence = Sigmoid(singleImageScores[offset_Confidence]);

            // collect the per-object-class scores, and convert to a PROBABILITY DISTRIBUTION
            float[] objClassScoreDistribution = new float[this._yoloSettings.class_count];
            for (int i = 0; i < this._yoloSettings.class_count; i++)
            {
                int offset = ((channel + YOLO_BOX_INFO_FEATURE_COUNT + i) * numCells) + (row * this._yoloSettings.column_count) + col;
                objClassScoreDistribution[i] = singleImageScores[offset];  // this is a logit for a confidence score
            }
            objClassScoreDistribution = Softmax(objClassScoreDistribution);

            // identify the top-scoring object class
            (int topClassIndex, float topClassScore) = objClassScoreDistribution
                           .Select((classScore, index) => (Index: index, Value: classScore))
                           .OrderByDescending(result => result.Value)
                           .First();

            // Produce the top object class LABEL ; if the client provided class label names, use those;
            // otherwise, just assign a stringized index like "0", "1", "2", ...
            string topClassLabel = (topClassIndex < this._object_class_labels.Length) ?
                                    this._object_class_labels[topClassIndex] : topClassIndex.ToString();

            float labelConfidence = boxConfidence * topClassScore;

            DebugPrint(3, "final bounding box : x = {0}, y = {1}, width = {2}, height = {3} ; class = '{4}, conf = {5}  ", boundingBoxDims.X, boundingBoxDims.Y, boundingBoxDims.Width, boundingBoxDims.Height, topClassLabel, labelConfidence);

            BoundingBox boundingBox = new BoundingBox();
            boundingBox.Dimensions = boundingBoxDims;
            boundingBox.Label = topClassLabel;
            boundingBox.Confidence = labelConfidence;
            boundingBox.ObjectClassScores = objClassScoreDistribution;

            return boundingBox;
        }


        /// <summary>
        /// FilterBoundingBoxes
        /// </summary>
        /// <param name="boxes"></param>
        /// <param name="maxBoxesPerImage"></param>
        /// <param name="maxIntersectOverUnion"></param>
        /// <returns></returns>
        private List<BoundingBox> FilterBoundingBoxes(List<BoundingBox> boxes, int maxBoxesPerImage, float maxIntersectOverUnion)
        {
            int activeCount = boxes.Count;
            bool[] isActiveBoxes = new bool[boxes.Count];

            for (int i = 0; i < isActiveBoxes.Length; i++)
            {
                isActiveBoxes[i] = true;
            }

            // SORT the bounding boxes from highest-to-lowest CONFIDENCE
            boxes = boxes.OrderByDescending(b => b.Confidence).ToList();

            List<BoundingBox> results = new List<BoundingBox>();
            for (int i = 0; i < boxes.Count; i++)
            {
                if (isActiveBoxes[i])
                {
                    BoundingBox boxA = boxes[i];
                    results.Add(boxA);

                    if (results.Count < maxBoxesPerImage)
                    {
                        // look at adjacent bounding boxes ?
                        for (int j = i + 1; j < boxes.Count; j++)
                        {
                            if (isActiveBoxes[j])
                            {
                                BoundingBox boxB = boxes[j];

                                if (IntersectionOverUnion(boxA.Dimensions, boxB.Dimensions) > maxIntersectOverUnion)
                                {
                                    isActiveBoxes[j] = false;
                                    activeCount--;

                                    if (activeCount <= 0)
                                    {
                                        break;
                                    }
                                }
                            }
                        }

                        if (activeCount <= 0)
                        {
                            break;
                        }
                    }
                    else 
                    {
                        break;
                    }
                }
            }

            return results;
        }



        /// <summary>
        /// SetObjectClassLabels
        /// </summary>
        /// <param name="labels"></param>
        public void SetObjectClassLabels(string[] labels)
        {
            // we do not require that labels size be equal to this._class_count ; if we are not given a label, lable will be "0", "1", "2", ...

            this._object_class_labels = labels;
        }


        /// <summary>
        /// SetObjectClassBoundingBoxColors - override the default color selection for bounding boxes
        /// </summary>
        /// <param name="colors">Array of color values, by object class index (as identified by the loaded YOLO model).
        ///                      You do NOT need to provide a color per object class; we will rotate through the set of colors provided.
        /// </param>
        public void SetObjectClassBoundingBoxColors(Color[] colors)
        {
            this._objectClassColors = colors;
        }


        /// <summary>
        /// SetBoundingBoxAspectRatios
        /// </summary>
        /// <param name="boundingBoxAspectRatios"></param>
        /// <returns></returns>
        public bool SetBoundingBoxAspectRatios(float[,] boundingBoxAspectRatios)
        {
            bool ok = false;

            if (boundingBoxAspectRatios.GetLength(0) != this._yoloSettings.boxes_per_cell)
            {
                DebugPrint(0, "ERROR in SetBoundingBoxAspectRatios() : boundingBoxAspectRatios must be same length as YoloModelSettings.boxes_per_cell ");
            }
            else if (boundingBoxAspectRatios.GetLength(1) != 2)
            {
                DebugPrint(0, "ERROR in SetBoundingBoxAspectRatios() : boundingBoxAspectRatios inner dimension must be size 2, containing (width, height) pairs ");
            }
            else
            {
                this._boundingBoxAspectRatios = boundingBoxAspectRatios;
                ok = true;
            }

            return ok;
        }


        /// <summary>
        /// OverlayRectangle - overlays a rectangle onto a background image
        /// </summary>
        /// <param name="backgroundImgToModify">the input image is modified</param>
        /// <param name="startX"></param>
        /// <param name="startY"></param>
        /// <param name="rectWidth">width including edge pixels</param>
        /// <param name="rectHeight">height including edge pixels</param>
        /// <param name="rectEdgeColor"></param>
        /// <param name="edgeWidthPixels"></param>
        public static void OverlayRectangle(ref Image backgroundImgToModify, int startX, int startY, int rectWidth, int rectHeight, System.Drawing.Color rectEdgeColor, int edgeWidthPixels)
        {
            Graphics g = Graphics.FromImage(backgroundImgToModify);
            System.Drawing.Pen pen = new System.Drawing.Pen(rectEdgeColor, edgeWidthPixels);
            g.DrawRectangle(pen, startX, startY, rectWidth - (edgeWidthPixels * 2), rectHeight - (edgeWidthPixels * 2));
        }


        /// <summary>
        /// ResizeImage
        /// </summary>
        /// <param name="image"></param>
        /// <param name="width"></param>
        /// <param name="height"></param>
        /// <param name="createSavableImage">if true, the returned Image will be savable and convertible to a dataUrl; if false, execution will be faster, and the Image will not be savable/convertible</param>
        /// <returns></returns>
        public static Image ResizeImage(Image image, int width, int height, bool createSavableImage = true)
        {
            Image sizedImage = null;

            try
            {
                Bitmap sizedBitmap = new Bitmap(width, height);

                using (Graphics graphics = Graphics.FromImage(sizedBitmap))  // (a Bitmap can be typecast directly to an Image)
                {
                    graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;
                    graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;  // ensures high-quality shrinking
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;

                    graphics.DrawImage(image, 0, 0, width, height);  // draw original image -> sizedBitmap

                    if (createSavableImage)
                    {
                        // Use the safe bitmap->image conversion (not image = bitmap, which basically works, but results in an image that cannot be converted to a DATAURL)
                        sizedImage = SafeBitmapToImage(sizedBitmap);
                    }
                    else
                    {
                        // this is much faster; but image will not be savable or convertible to a dataUrl
                        sizedImage = (Image)sizedBitmap;
                    }
                }
            }
            catch (Exception e)
            {
                SimpleUtils.Diagnostics.DumpException(e);
                sizedImage = null;
            }

            return sizedImage;
        }


        /// <summary>
        /// SafeBitmapToImage - convert a bitmap to a *savable* Image (unlike a direct typecast from Bitmap to Image)
        /// </summary>
        /// <param name="bitmap"></param>
        /// <returns></returns>
        public static Image SafeBitmapToImage(Bitmap bitmap)
        {
            //
            // We could just do :  image = (Image)bitmap;  // (a Bitmap can be typecast directly to an Image)
            // HOWEVER, the resulting image cannot be formatted as a DATAURL (via Jpeg, Bmp, or any format)
            // (specifically : image.Save(memstream, ImageFormat.Jpeg) crashes).
            // This is true WHETHER OR NOT we just RESIZED the image (i.e. Image.FromFile("?.jpg") is also unusable in this regard).
            // So convert bitmap to an actual Jpeg-formatted Image, using a CODEC.
            // Following EXAMPLE at : https://stackoverflow.com/questions/3075906/using-c-sharp-how-can-i-resize-a-jpeg-image
            //
            if (MyEncoderParams.Param[0] == null)
            {
                // initialize this static param once
                MyEncoderParams.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 100L);
            }
            MemoryStream memstream = new MemoryStream();
            bitmap.Save(memstream, MyImgCodecInfo, MyEncoderParams);
            Image image = System.Drawing.Image.FromStream(memstream);

            return image;
        }


        /// <summary>
        /// Sigmoid - converts from a logit value (in range [-inf, +inf]) to a probability (in range [0, 1])
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        private float Sigmoid(float value)
        {
            var k = (float)Math.Exp(value);
            return k / (1.0f + k);
        }


        /// <summary>
        /// Softmax
        /// </summary>
        /// <param name="values"></param>
        /// <returns></returns>
        private float[] Softmax(float[] values)
        {
            var maxVal = values.Max();
            var exp = values.Select(v => Math.Exp(v - maxVal));
            var sumExp = exp.Sum();

            return exp.Select(v => (float)(v / sumExp)).ToArray();
        }


        /// <summary>
        /// IntersectionOverUnion
        /// </summary>
        /// <param name="rectA"></param>
        /// <param name="rectB"></param>
        /// <returns></returns>
        private float IntersectionOverUnion(Rectangle rectA, Rectangle rectB)
        {
            float result = 0.0f;

            int areaA = rectA.Width * rectA.Height;
            int areaB = rectB.Width * rectB.Height;

            if ((areaA > 0) && (areaB > 0))
            {
                // these are finding MIN/MAX of the INTERSECTION (if any)
                int minX = Math.Max(rectA.Left, rectB.Left);
                int minY = Math.Max(rectA.Top, rectB.Top);
                int maxX = Math.Min(rectA.Right, rectB.Right);
                int maxY = Math.Min(rectA.Bottom, rectB.Bottom);

                int intersectionArea = Math.Max(maxX - minX, 0) * Math.Max(maxY - minY, 0);
                int unionArea = (areaA + areaB - intersectionArea);

                result =  (float)intersectionArea / (float)unionArea;
            }

            return result;
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
                Console.WriteLine("OnnxObjectDetection : " + format, varArgs);
            }
        }


    }
}
