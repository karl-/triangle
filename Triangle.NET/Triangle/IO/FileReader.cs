﻿// -----------------------------------------------------------------------
// <copyright file="io.cs" company="">
// Original Triangle code by Jonathan Richard Shewchuk, http://www.cs.cmu.edu/~quake/triangle.html
// Triangle.NET code by Christian Woltering, http://home.edo.tu-dortmund.de/~woltering/triangle/
// </copyright>
// -----------------------------------------------------------------------

namespace TriangleNet.IO
{
    using System;
    using System.IO;
    using System.Globalization;
    using TriangleNet.Data;
    using TriangleNet.Log;
    using TriangleNet.Geometry;
    using System.Collections.Generic;

    /// <summary>
    /// Helper for reading Triangle files.
    /// </summary>
    public static class FileReader
    {
        static NumberFormatInfo nfi = CultureInfo.InvariantCulture.NumberFormat;
        static int startIndex = 0;

        /// <summary>
        /// Read the input data from a file, which may be a .node or .poly file.
        /// </summary>
        /// <param name="filename">The file to read.</param>
        /// <remarks>Will NOT read associated files by default.</remarks>
        public static InputGeometry ReadFile(string filename)
        {
            return ReadFile(filename, false);
        }

        /// <summary>
        /// Read the input data from a file, which may be a .node or .poly file.
        /// </summary>
        /// <param name="filename">The file to read.</param>
        /// <param name="readsupp">Read associated files (ele, area, neigh).</param>
        public static InputGeometry ReadFile(string filename, bool readsupp)
        {
            string ext = Path.GetExtension(filename);

            if (ext == ".node")
            {
                return ReadNodeFile(filename, readsupp);
            }
            else if (ext == ".poly")
            {
                return ReadPolyFile(filename, readsupp, readsupp);
            }

            throw new NotSupportedException("File format '" + ext + "' not supported.");
        }

        static bool TryReadLine(StreamReader reader, out string[] token)
        {
            token = null;

            if (reader.EndOfStream)
            {
                return false;
            }

            string line = reader.ReadLine().Trim();

            while (String.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            {
                if (reader.EndOfStream)
                {
                    return false;
                }

                line = reader.ReadLine().Trim();
            }

            token = line.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);

            return true;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="data"></param>
        /// <param name="index"></param>
        /// <param name="line"></param>
        /// <param name="n">Number of point attributes</param>
        static void ReadVertex(InputGeometry data, int index, string[] line, int n)
        {
            double x = double.Parse(line[1], nfi);
            double y = double.Parse(line[2], nfi);
            int mark = 0;

            // Read the vertex attributes.
            for (int j = 0; j < n; j++)
            {
                if (line.Length > 3 + j)
                {
                    // TODO:
                    //vertex.attributes[j] = double.Parse(line[3 + j]);
                }
            }

            // Read a vertex marker.
            if (line.Length > 3 + n)
            {
                mark = int.Parse(line[3 + n]);
            }

            data.AddPoint(x, y, mark);
        }

        /// <summary>
        /// Read the vertices from a file, which may be a .node or .poly file.
        /// </summary>
        /// <param name="nodefilename"></param>
        /// <remarks>Will NOT read associated .ele by default.</remarks>
        public static InputGeometry ReadNodeFile(string nodefilename)
        {
            return ReadNodeFile(nodefilename, false);
        }

        /// <summary>
        /// Read the vertices from a file, which may be a .node or .poly file.
        /// </summary>
        /// <param name="nodefilename"></param>
        /// <param name="readElements"></param>
        public static InputGeometry ReadNodeFile(string nodefilename, bool readElements)
        {
            InputGeometry data;

            startIndex = 0;

            string[] line;
            int invertices = 0, attributes = 0, nodemarkers = 0;

            using (StreamReader reader = new StreamReader(nodefilename))
            {
                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file.");
                }

                // Read number of vertices, number of dimensions, number of vertex
                // attributes, and number of boundary markers.
                invertices = int.Parse(line[0]);

                if (invertices < 3)
                {
                    throw new Exception("Input must have at least three input vertices.");
                }

                if (line.Length > 1)
                {
                    if (int.Parse(line[1]) != 2)
                    {
                        throw new Exception("Triangle only works with two-dimensional meshes.");
                    }
                }

                if (line.Length > 2)
                {
                    attributes = int.Parse(line[2]);
                }

                if (line.Length > 3)
                {
                    nodemarkers = int.Parse(line[3]);
                }

                data = new InputGeometry(invertices);

                // Read the vertices.
                if (invertices > 0)
                {
                    for (int i = 0; i < invertices; i++)
                    {
                        if (!TryReadLine(reader, out line))
                        {
                            throw new Exception("Can't read input file (vertices).");
                        }

                        if (line.Length < 3)
                        {
                            throw new Exception("Invalid vertex.");
                        }

                        if (i == 0)
                        {
                            startIndex = int.Parse(line[0], nfi);
                        }

                        ReadVertex(data, i, line, attributes);
                    }
                }
            }

            if (readElements)
            {
                // Read area file
                string elefile = Path.ChangeExtension(nodefilename, ".ele");
                if (File.Exists(elefile))
                {
                    ReadEleFile(elefile, true);
                }
            }

            return data;
        }

        /// <summary>
        /// Read the vertices and segments from a .poly file.
        /// </summary>
        /// <param name="polyfilename"></param>
        /// <remarks>Will NOT read associated .ele by default.</remarks>
        public static InputGeometry ReadPolyFile(string polyfilename)
        {
            return ReadPolyFile(polyfilename, false, false);
        }

        /// <summary>
        /// Read the vertices and segments from a .poly file.
        /// </summary>
        /// <param name="polyfilename"></param>
        /// <param name="readElements">If true, look for an associated .ele file.</param>
        /// <remarks>Will NOT read associated .area by default.</remarks>
        public static InputGeometry ReadPolyFile(string polyfilename, bool readElements)
        {
            return ReadPolyFile(polyfilename, readElements, false);
        }

        /// <summary>
        /// Read the vertices and segments from a .poly file.
        /// </summary>
        /// <param name="polyfilename"></param>
        /// <param name="readElements">If true, look for an associated .ele file.</param>
        /// <param name="readElements">If true, look for an associated .area file.</param>
        public static InputGeometry ReadPolyFile(string polyfilename, bool readElements, bool readArea)
        {
            // Read poly file
            InputGeometry data;

            startIndex = 0;

            string[] line;
            int invertices = 0, attributes = 0, nodemarkers = 0;

            using (StreamReader reader = new StreamReader(polyfilename))
            {
                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file.");
                }

                // Read number of vertices, number of dimensions, number of vertex
                // attributes, and number of boundary markers.
                invertices = int.Parse(line[0]);

                if (line.Length > 1)
                {
                    if (int.Parse(line[1]) != 2)
                    {
                        throw new Exception("Triangle only works with two-dimensional meshes.");
                    }
                }

                if (line.Length > 2)
                {
                    attributes = int.Parse(line[2]);
                }

                if (line.Length > 3)
                {
                    nodemarkers = int.Parse(line[3]);
                }

                // Read the vertices.
                if (invertices > 0)
                {
                    data = new InputGeometry(invertices);

                    for (int i = 0; i < invertices; i++)
                    {
                        if (!TryReadLine(reader, out line))
                        {
                            throw new Exception("Can't read input file (vertices).");
                        }

                        if (line.Length < 3)
                        {
                            throw new Exception("Invalid vertex.");
                        }

                        if (i == 0)
                        {
                            // Set the start index!
                            startIndex = int.Parse(line[0], nfi);
                        }

                        ReadVertex(data, i, line, attributes);
                    }
                }
                else
                {
                    // If the .poly file claims there are zero vertices, that means that
                    // the vertices should be read from a separate .node file.
                    string nodefile = Path.ChangeExtension(polyfilename, ".node");
                    data = ReadNodeFile(nodefile);
                    invertices = data.Count;
                }

                if (data.Points == null)
                {
                    throw new Exception("No nodes available.");
                }

                // Read the segments from a .poly file.

                // Read number of segments and number of boundary markers.
                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file (segments).");
                }

                int insegments = int.Parse(line[0]);

                int segmentmarkers = 0;
                if (line.Length > 1)
                {
                    segmentmarkers = int.Parse(line[1]);
                }

                int end1, end2, mark;
                // Read and insert the segments.
                for (int i = 0; i < insegments; i++)
                {
                    if (!TryReadLine(reader, out line))
                    {
                        throw new Exception("Can't read input file (segments).");
                    }

                    if (line.Length < 3)
                    {
                        throw new Exception("Segment has no endpoints.");
                    }

                    // TODO: startIndex ok?
                    end1 = int.Parse(line[1]) - startIndex;
                    end2 = int.Parse(line[2]) - startIndex;
                    mark = 0;

                    if (segmentmarkers > 0 && line.Length > 3)
                    {
                        mark = int.Parse(line[3]);
                    }

                    if ((end1 < 0) || (end1 >= invertices))
                    {
                        if (Behavior.Verbose)
                        {
                            SimpleLog.Instance.Warning("Invalid first endpoint of segment.",
                                "MeshReader.ReadPolyfile()");
                        }
                    }
                    else if ((end2 < 0) || (end2 >= invertices))
                    {
                        if (Behavior.Verbose)
                        {
                            SimpleLog.Instance.Warning("Invalid second endpoint of segment.",
                                "MeshReader.ReadPolyfile()");
                        }
                    }
                    else
                    {
                        data.AddSegment(end1, end2, mark);
                    }
                }

                // Read holes from a .poly file.

                // Read the holes.
                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file (holes).");
                }

                int holes = int.Parse(line[0]);
                if (holes > 0)
                {
                    for (int i = 0; i < holes; i++)
                    {
                        if (!TryReadLine(reader, out line))
                        {
                            throw new Exception("Can't read input file (holes).");
                        }

                        if (line.Length < 3)
                        {
                            throw new Exception("Invalid hole.");
                        }

                        data.AddHole(double.Parse(line[1], nfi),
                            double.Parse(line[2], nfi));
                    }
                }

                // Read area constraints (optional).
                if (TryReadLine(reader, out line))
                {
                    int regions = int.Parse(line[0]);

                    if (regions > 0)
                    {
                        for (int i = 0; i < regions; i++)
                        {
                            if (!TryReadLine(reader, out line))
                            {
                                throw new Exception("Can't read input file (region).");
                            }

                            if (line.Length < 5)
                            {
                                throw new Exception("Invalid region.");
                            }

                            data.AddRegion(
                                // Region x and y
                                double.Parse(line[1]),
                                double.Parse(line[2]),
                                // Region attribute
                                double.Parse(line[3]),
                                // Region area constraint
                                double.Parse(line[4]));
                        }
                    }
                }
            }

            // Read ele file
            if (readElements)
            {
                string elefile = Path.ChangeExtension(polyfilename, ".ele");
                if (File.Exists(elefile))
                {
                    ReadEleFile(elefile, readArea);
                }
            }

            return data;
        }

        public static List<ITriangle> ReadEleFile(string elefilename)
        {
            return ReadEleFile(elefilename, false);
        }

        /// <summary>
        /// Read the elements from an .ele file.
        /// </summary>
        /// <param name="elefilename"></param>
        /// <param name="data"></param>
        /// <param name="readArea"></param>
        private static List<ITriangle> ReadEleFile(string elefilename, bool readArea)
        {
            int intriangles = 0, attributes = 0;

            List<ITriangle> triangles;

            using (StreamReader reader = new StreamReader(elefilename))
            {
                // Read number of elements and number of attributes.
                string[] line;

                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file (elements).");
                }

                intriangles = int.Parse(line[0]);

                // We irgnore index 1 (number of nodes per triangle)
                attributes = 0;
                if (line.Length > 2)
                {
                    attributes = int.Parse(line[2]);
                }

                triangles = new List<ITriangle>(intriangles);

                InputTriangle tri;

                // Read triangles.
                for (int i = 0; i < intriangles; i++)
                {
                    if (!TryReadLine(reader, out line))
                    {
                        throw new Exception("Can't read input file (elements).");
                    }

                    if (line.Length < 4)
                    {
                        throw new Exception("Triangle has no nodes.");
                    }

                    // TODO: startIndex ok?
                    tri = new InputTriangle(
                        int.Parse(line[1]) - startIndex,
                        int.Parse(line[2]) - startIndex,
                        int.Parse(line[3]) - startIndex);

                    // Read triangle attributes
                    if (attributes > 0)
                    {
                        for (int j = 0; j < attributes; j++)
                        {
                            tri.attributes = new double[attributes];

                            if (line.Length > 4 + j)
                            {
                                tri.attributes[j] = double.Parse(line[4 + j]);
                            }
                        }
                    }

                    triangles.Add(tri);
                }
            }

            // Read area file
            if (readArea)
            {
                string areafile = Path.ChangeExtension(elefilename, ".area");
                if (File.Exists(areafile))
                {
                    ReadAreaFile(areafile, intriangles);
                }
            }

            return triangles;
        }

        /// <summary>
        /// Read the area constraints from an .area file.
        /// </summary>
        /// <param name="areafilename"></param>
        /// <param name="intriangles"></param>
        /// <param name="data"></param>
        private static double[] ReadAreaFile(string areafilename, int intriangles)
        {
            double[] data = null;

            using (StreamReader reader = new StreamReader(areafilename))
            {
                string[] line;

                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file (area).");
                }

                if (int.Parse(line[0]) != intriangles)
                {
                    SimpleLog.Instance.Warning("Number of area constraints doesn't match number of triangles.",
                        "ReadAreaFile()");
                    return null;
                }

                data = new double[intriangles];

                // Read area constraints.
                for (int i = 0; i < intriangles; i++)
                {
                    if (!TryReadLine(reader, out line))
                    {
                        throw new Exception("Can't read input file (area).");
                    }

                    if (line.Length != 2)
                    {
                        throw new Exception("Triangle has no nodes.");
                    }

                    data[i] = double.Parse(line[1], nfi);
                }
            }

            return data;
        }

        public static List<Edge> ReadEdgeFile(string edgeFile, int invertices)
        {
            // Read poly file
            List<Edge> data = null;

            startIndex = 0;

            string[] line;

            using (StreamReader reader = new StreamReader(edgeFile))
            {
                // Read the edges from a .edge file.

                // Read number of segments and number of boundary markers.
                if (!TryReadLine(reader, out line))
                {
                    throw new Exception("Can't read input file (segments).");
                }

                int inedges = int.Parse(line[0]);

                int edgemarkers = 0;
                if (line.Length > 1)
                {
                    edgemarkers = int.Parse(line[1]);
                }

                if (inedges > 0)
                {
                    data = new List<Edge>(inedges);
                }

                int end1, end2, mark;
                // Read and insert the segments.
                for (int i = 0; i < inedges; i++)
                {
                    if (!TryReadLine(reader, out line))
                    {
                        throw new Exception("Can't read input file (segments).");
                    }

                    if (line.Length < 3)
                    {
                        throw new Exception("Segment has no endpoints.");
                    }

                    // TODO: startIndex ok?
                    end1 = int.Parse(line[1]) - startIndex;
                    end2 = int.Parse(line[2]) - startIndex;
                    mark = 0;

                    if (edgemarkers > 0 && line.Length > 3)
                    {
                        mark = int.Parse(line[3]);
                    }

                    if ((end1 < 0) || (end1 >= invertices))
                    {
                        if (Behavior.Verbose)
                        {
                            SimpleLog.Instance.Warning("Invalid first endpoint of segment.",
                                "MeshReader.ReadPolyfile()");
                        }
                    }
                    else if ((end2 < 0) || (end2 >= invertices))
                    {
                        if (Behavior.Verbose)
                        {
                            SimpleLog.Instance.Warning("Invalid second endpoint of segment.",
                                "MeshReader.ReadPolyfile()");
                        }
                    }
                    else
                    {
                        data.Add(new Edge(end1, end2, mark));
                    }
                }
            }

            return data;
        }
    }
}
