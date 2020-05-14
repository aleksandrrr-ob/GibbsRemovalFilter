using System;
using System.Threading.Tasks;

//2020, A. Obukhov
//Target platform: .NET Standard 2.1 (2.1 not really needed, you can tweak a couple of lines to make this code work in previous versions)

namespace GibbsRemovalFilterNS
{
  #region ImageWrapper

  //wraps data for one-channel image
  internal class ImageWrapper
  {
    public readonly int Width;
    public readonly int Height;
    private readonly int DataOffset;
    private readonly int Stride;
    private readonly float[] Data = null;

    //borderOffset - offset to eliminate bound checks
    //image scan can be even slower, so it's just to make filter code pretty
    public ImageWrapper(int width, int height, int borderOffset)
    {
      Width = width;
      Height = height;
      Stride = Width + 2 * borderOffset;
      DataOffset = Stride * borderOffset + borderOffset;
      Data = new float[Stride * (Height + 2 * borderOffset)];
    }

    public float this[int x, int y]
    {
      get { return Data[DataOffset + x + y * Stride]; }
      set { Data[DataOffset + x + y * Stride] = value; }
    }

    public void CopyTo(ushort[] destination)
    {
      int offsetData;
      int offsetArray = 0;
      for (int y = 0; y < Height; y++)
      {
        offsetData = DataOffset + Stride * y;
        for (int x = 0; x < Width; x++)
          destination[offsetArray++] = (ushort)Math.Clamp(Math.Round(Data[offsetData++]), 0.0f, 65535.0f);
      }
    }

    public void CopyFrom(ushort[] source)
    {
      int offsetData;
      int offsetArray = 0;
      for (int y = 0; y < Height; y++)
      {
        offsetData = DataOffset + Stride * y;
        for (int x = 0; x < Width; x++)
          Data[offsetData++] = source[offsetArray++];
      }
    }
  }

  #endregion

  /**
   * The <code>GibbsRemovalFilter</code> class Implements image filter to eliminate 
   * ringing artifacts appearing due to Gibbs phenomenon and other possible reasons.
   */
  public class GibbsRemovalFilter
  {
    /**
     * Maximum pixel size of filter window.
     */
    public const int MAX_FILTER_WINDOW = 100;

    private readonly ImageWrapper image;
    private readonly ImageWrapper dx; // dI/dx partial derivative
    private readonly ImageWrapper dy; // dI/dy partial derivative

    /**
     * Creates a new <code>GibbsRemovalFilter</code> object.
     * @param imageData Linear array image representation.
     * @param width Image width.
     * @param height Image height.
     * @throw ArgumentNullException if imageData is null.
     * @throw ArgumentException if image has zero size or width/height parameters are wrong.
     */
    public GibbsRemovalFilter(ushort[] imageData, int width, int height)
    {
      if (imageData == null)
        throw new ArgumentNullException("imageData");
      if (imageData.Length == 0)
        throw new ArgumentException("Zero size image");
      if (imageData.Length != width * height || width < 0 || height < 0)
        throw new ArgumentException("Wrong width and/or height value (expected: imageData.Length = width * height)");

      image = new ImageWrapper(width, height, MAX_FILTER_WINDOW + 2);
      image.CopyFrom(imageData);
      dx = new ImageWrapper(width, height, 0);
      dy = new ImageWrapper(width, height, 0);

      CalculateDerivatives();
    }

    private void CalculateDerivatives()
    {
      if (image == null)
        return;

      float[] p = { 0.037659f, 0.249153f, 0.426375f, 0.249153f, 0.037659f };
      float[] d1 = { 0.109604f, 0.276691f, 0.000000f, -0.276691f, -0.109604f };

      Conv2(p, d1, dx);
      Conv2(d1, p, dy);

      Parallel.For(0, image.Height, (y) =>
      {
        float norm;
        for (int x = 0; x < image.Width; x++)
        {
          norm = (float)(1.0 / (Math.Sqrt(dx[x, y] * dx[x, y] + dy[x, y] * dy[x, y]) + 0.000001));
          dx[x, y] *= norm;
          dy[x, y] *= norm;
        }
      });
    }

    //2d image convolution with 5x5 kernel
    private void Conv2(float[] vcol, float[] vrow, ImageWrapper res)
    {
      ImageWrapper temp_frame = new ImageWrapper(res.Width, res.Height, 2);
      Parallel.For(0, res.Height, (y) =>
      {
        for (int x = 0; x < res.Width; x++)
        {
          temp_frame[x, y] =
            image[x, y - 2] * vcol[0] +
            image[x, y - 1] * vcol[1] +
            image[x, y]     * vcol[2] +
            image[x, y + 1] * vcol[3] +
            image[x, y + 2] * vcol[4];
        }
      });
      Parallel.For(0, res.Width, (x) =>
      {
        for (int y = 0; y < res.Height; y++)
        {
          res[x, y] = 
            temp_frame[x - 2, y] * vrow[0] +
            temp_frame[x - 1, y] * vrow[1] +
            temp_frame[x, y]     * vrow[2] +
            temp_frame[x + 1, y] * vrow[3] +
            temp_frame[x + 2, y] * vrow[4];
        }
      });
    }

    //Gets estimated value for given pixel, direction and window size
    private float GetFilteredValue(int x, int y, int window, float kx, float ky)
    {
      int count = 0;
      float value = 0;
      int px, py;
      int px_prev = x, py_prev = y;

      for (int i = 1; i <= window; i++)
      {
        px = (int)Math.Round(x + kx * i);
        py = (int)Math.Round(y + ky * i);
        if (px != px_prev || py != py_prev)
        {
          value += image[px, py];
          count++;
          px_prev = px; py_prev = py;
        }
      }

      return count == 0 ? image[x, y] : value / count;
    }

    /**
     * Makes processed image and writes it to output array.
     * Note: Gaussian blur needed after processing. 
     * @param varEstimationWindow Variation estimation window size (expected: [0...MAX_FILTER_WINDOW]).
     * @param outputImage Output image data.
     * @throw ArgumentNullException if outputImage is null.
     * @throw ArgumentException if outputImage has wrong size or parameters are out of expected ranges.
     */
    public void ProcessImage(int varEstimationWindow, ushort[] outputImage)
    {
      if (outputImage == null)
        throw new ArgumentNullException("outputImage");
      if (outputImage.Length != image.Width * image.Height)
        throw new ArgumentException("Wrong array size (expected: outputImage.Length = width * height)");
      if (varEstimationWindow < 0 || varEstimationWindow > MAX_FILTER_WINDOW)
        throw new ArgumentException("Wrong value of varEstimationWindow parameter (expected: [0...MAX_FILTER_WINDOW])");

      Parallel.For(0, image.Height, (y) =>
      {
        for (int x = 0; x < image.Width; x++)
        {
          float kx = dx[x, y];
          float ky = dy[x, y];

          float value1 = GetFilteredValue(x, y, varEstimationWindow, kx, ky);
          float value2 = GetFilteredValue(x, y, varEstimationWindow, -kx, -ky);

          outputImage[x + image.Width * y] = (ushort)Math.Clamp(Math.Round(Math.Abs(value1 - image[x, y]) < Math.Abs(value2 - image[x, y]) ? value1 : value2), 0.0f, 65535.0f);
        }
      });
    }
  }
}
