﻿using NetTopologySuite.Geometries;
using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Ciribob.DCS.SimpleRadio.Standalone.Client.Overlord.Util
{
    class Geospatial
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        const double EarthRadius = 6378137.0;
        const double DegreesToRadians = 0.0174532925;
        const double RadiansToDegrees = 57.2957795;
        /// <summary>
        /// Calculates the new-point from a given source at a given range (meters) and bearing (degrees).
        /// Taken from https://adhamhurani.blogspot.com/2010/08/c-how-to-add-distance-and-calculate-new.html
        /// </summary>
        /// <param name="source">Orginal Point</param>
        /// <param name="range">Range in meters</param>
        /// <param name="bearing">Bearing in degrees (Must be true and not magnetic)</param>
        /// <returns>End-point from the source given the desired range and bearing.</returns>
        public static Point CalculatePointFromSource(Point source, double range, double bearing)
        {
            // Our Point class with PostGIS is lon/lat format so X is lon and Y is lat... God this is confusing.
            // Why can't they just use the names lat/lon instead of this 50/50 might-be-right stuff.
            double latA = source.Y * DegreesToRadians;
            double lonA = source.X * DegreesToRadians;
            double angularDistance = range / EarthRadius;
            double trueCourse = bearing * DegreesToRadians;

            double lat = Math.Asin(Math.Sin(latA) * Math.Cos(angularDistance) + Math.Cos(latA) * Math.Sin(angularDistance) * Math.Cos(trueCourse));

            double dlon = Math.Atan2(Math.Sin(trueCourse) * Math.Sin(angularDistance) * Math.Cos(latA), Math.Cos(angularDistance) - Math.Sin(latA) * Math.Sin(lat));
            double lon = ((lonA + dlon + Math.PI) % (Math.PI * 2)) - Math.PI;

            return new Point(lon * RadiansToDegrees, lat * RadiansToDegrees);
        }


        class GeoPoint : Geo.Abstractions.Interfaces.IPosition
        {
            private readonly Point Position;
            public GeoPoint(Point position)
            {
                Position = position;
            }

            public Geo.Coordinate GetCoordinate()
            {
                // Remember Point is Lon/Lat
                return new Geo.Coordinate(Position.Coordinate.Y, Position.Coordinate.X);
            }
        }

        public static double TrueToMagnetic(Point position, double bearing)
        {
            var magnetic = bearing - CalculateOffset(position);
            return magnetic;
        }

        public static double MagneticToTrue(Point position, double bearing)
        {
            var magnetic = bearing + CalculateOffset(position);
            return magnetic;
        }

        private static double CalculateOffset(Point position)
        {
            var geopoint = new GeoPoint(position);
            var calculator = new Geo.Geomagnetism.WmmGeomagnetismCalculator(Geo.Geodesy.Spheroid.Wgs84);
            var result = calculator.TryCalculate(geopoint, DateTime.UtcNow);
            return result.Declination;
        }
    }
}
