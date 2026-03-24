using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Media3D;

namespace AntennaSimulatorApp
{
    public class OffsetConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            // Expected: X, Y, Z (optional)
            double x = 0, y = 0, z = 0;
            
            if (values.Length > 0 && values[0] is double vX) x = vX;
            if (values.Length > 1 && values[1] is double vY) y = vY;
            if (values.Length > 2 && values[2] is double vZ) z = vZ;

            return new Point3D(x, y, z);
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}