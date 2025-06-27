// --------------------------------------------------------------------------------------
// Copyright 2025 Daniel Kereama
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
//     http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// Author      : Daniel Kereama
// Created     : 2025-06-27
// --------------------------------------------------------------------------------------
using System.Drawing;
using System.Drawing.Imaging;

namespace IA.Vision.App.Utils
{
    public class BayerImageConverter
    {
        // Convert raw BayerRG8 bytes to a Bitmap
        public Bitmap ConvertRawToBitmap(byte[] rawData, int width, int height)
        {
            // Check if the file size matches the expected resolution
            if (rawData.Length != width * height)
            {
                throw new Exception($"File size ({rawData.Length} bytes) does not match expected size ({width * height} bytes) for 3208x160 BayerRG8.");
            }

            // Create a Bitmap for the output RGB image
            Bitmap bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);

            // Lock the bitmap's bits for fast access
            var bitmapData = bitmap.LockBits(new Rectangle(0, 0, width, height),
                                             System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                             bitmap.PixelFormat);

            unsafe
            {
                byte* ptr = (byte*)bitmapData.Scan0;

                // Simple nearest-neighbor demosaicing for Bayer RGGB pattern
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int index = y * width + x;
                        byte r, g, b;

                        // Bayer RGGB pattern:
                        // R G
                        // G B
                        if (y % 2 == 0 && x % 2 == 0) // Red pixel
                        {
                            r = rawData[index];
                            g = (x + 1 < width) ? rawData[index + 1] : rawData[index]; // Neighboring green
                            b = (y + 1 < height) ? rawData[index + width] : rawData[index]; // Neighboring blue
                        }
                        else if (y % 2 == 0 && x % 2 == 1) // Green pixel (top row)
                        {
                            g = rawData[index];
                            r = (x - 1 >= 0) ? rawData[index - 1] : rawData[index]; // Neighboring red
                            b = (y + 1 < height && x + 1 < width) ? rawData[index + width + 1] : rawData[index]; // Neighboring blue
                        }
                        else if (y % 2 == 1 && x % 2 == 0) // Green pixel (bottom row)
                        {
                            g = rawData[index];
                            r = (x + 1 < width) ? rawData[index + 1] : rawData[index]; // Neighboring red
                            b = (y - 1 >= 0) ? rawData[index - width] : rawData[index]; // Neighboring blue
                        }
                        else // Blue pixel
                        {
                            b = rawData[index];
                            r = (y - 1 >= 0 && x - 1 >= 0) ? rawData[index - width - 1] : rawData[index]; // Neighboring red
                            g = (x - 1 >= 0) ? rawData[index - 1] : rawData[index]; // Neighboring green
                        }

                        // Write RGB values to the bitmap (BGR order due to Format24bppRgb)
                        int pixelOffset = (y * bitmapData.Stride) + (x * 3);
                        ptr[pixelOffset] = b;     // Blue
                        ptr[pixelOffset + 1] = g; // Green
                        ptr[pixelOffset + 2] = r; // Red
                    }
                }
            }

            // Unlock the bitmap
            bitmap.UnlockBits(bitmapData);

            return bitmap;
        }

        /*
         * Converts a BayerRG8 raw image (grayscale data with an RGGB pattern) into an RGB bitmap.
         * BayerRG8 is a common format used in image sensors, where each pixel captures only one color
         * (Red, Green, or Blue), and neighboring pixels are used to reconstruct full-color values.
         *
         * This implementation uses a simple nearest-neighbor approach:
         * - Red pixels remain red.
         * - Blue pixels remain blue.
         * - Green pixels remain green.
         * - Missing color values are not interpolated, leading to a "mosaic" effect.
         */

        public static Bitmap ConvertBayerToRGB(byte[] bayerData, int width, int height)
        {
            Bitmap rgbImage = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            int index = 0;

            // Scale factor adjusts brightness; gamma correction improves perceived brightness.
            byte scale = 2;
            double gamma = 2.2;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    byte r = 0, g = 0, b = 0;

                    /*
                     * The Bayer pattern alternates rows:
                     * - Even rows: R G R G (Red-Green)
                     * - Odd rows:  G B G B (Green-Blue)
                     *
                     * Pixels are assigned based on their position in the pattern.
                     */
                    if (y % 2 == 0) // Red-Green row
                    {
                        if (x % 2 == 0)
                            r = (byte)Math.Min(255, bayerData[index] * scale); // Red pixel
                        else
                            g = (byte)Math.Min(255, bayerData[index] * scale); // Green pixel
                    }
                    else // Green-Blue row
                    {
                        if (x % 2 == 0)
                            b = (byte)Math.Min(255, bayerData[index] * scale); // Blue pixel
                        else
                            g = (byte)Math.Min(255, bayerData[index] * scale); // Green pixel
                    }

                    // Apply gamma correction for better brightness perception
                    r = ApplyGammaCorrection(r, gamma);
                    g = ApplyGammaCorrection(g, gamma);
                    b = ApplyGammaCorrection(b, gamma);

                    // Set the processed pixel color in the output image
                    rgbImage.SetPixel(x, y, Color.FromArgb(r, g, b));

                    index++;
                }
            }

            return rgbImage;
        }

        /*
         * Applies gamma correction to a pixel value.
         * Gamma correction adjusts brightness in a non-linear way to match human vision perception.
         * A gamma of 2.2 is commonly used for display correction.
         */

        public static byte ApplyGammaCorrection(byte value, double gamma = 2.2)
        {
            return (byte)(255.0 * Math.Pow(value / 255.0, 1.0 / gamma));
        }

        /*
         * Saves the processed image in PNG format.
         */

        public static void SaveAsPNG(Bitmap image, string filePath)
        {
            image.Save(filePath, ImageFormat.Png);
            Console.WriteLine($"Image saved to {filePath}");
        }
    }
}

