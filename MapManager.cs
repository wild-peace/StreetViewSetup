using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using Coordinate = NetTopologySuite.Geometries.Coordinate;
// 使用别名解决 Point 类型冲突
using NTSPoint = NetTopologySuite.Geometries.Point;
using DrawingPoint = System.Drawing.Point;
using GeoAPI.Geometries;

namespace LocalStreetViewApp
{
    // 街景节点结构
    public class StreetNode
    {
        public int Id;
        public double Lon;
        public double Lat;
        public string ImagePath;        // 绑定的街景图片路径
        public double DistanceToImage;  // 图片到节点距离
    }
    public class StreetLine
    {
        public int Id;
        public NetTopologySuite.Geometries.Coordinate[] Coordinates;  // 存储线的坐标点数组
    }
    public class MapManager
    {
        public List<StreetNode> Nodes = new List<StreetNode>();
        public List<StreetLine> Lines = new List<StreetLine>();
        private double zoomLevel = 1.0;
        private double viewCenterX = 0;
        private double viewCenterY = 0;
        private bool isViewInitialized = false;
       
        public void LoadShapefile(string shpPath)
        {
            try
            {
                Nodes.Clear();
                Console.WriteLine($"开始加载SHP文件: {shpPath}");

                if (!File.Exists(shpPath))
                {
                    Console.WriteLine($"SHP文件不存在: {shpPath}");
                    return;
                }

                // 检查相关文件是否存在
                string directory = Path.GetDirectoryName(shpPath);
                string baseName = Path.GetFileNameWithoutExtension(shpPath);
                string shxPath = Path.Combine(directory, baseName + ".shx");
                string dbfPath = Path.Combine(directory, baseName + ".dbf");

                Console.WriteLine($"相关文件检查:");
                Console.WriteLine($"   SHX文件: {shxPath} - 存在: {File.Exists(shxPath)}");
                Console.WriteLine($"   DBF文件: {dbfPath} - 存在: {File.Exists(dbfPath)}");

                if (!File.Exists(shxPath) || !File.Exists(dbfPath))
                {
                    Console.WriteLine($"SHP文件不完整，缺少必要的辅助文件");
                    return;
                }

                var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default);
                int id = 0;
                int recordCount = 0;

                // 获取字段名称信息
                var header = reader.DbaseHeader;
                Console.WriteLine($"属性字段信息:");
                for (int i = 0; i < header.Fields.Length; i++)
                {
                    var field = header.Fields[i];
                    Console.WriteLine($"  字段 {i}: {field.Name} ({field.Type})");
                }

                Console.WriteLine($"开始读取要素...");

                while (reader.Read())
                {
                    recordCount++;
                    var geom = reader.Geometry as NTSPoint;
                    if (geom == null)
                    {
                        Console.WriteLine($"  记录 {recordCount}: 不是点要素，跳过");
                        continue;
                    }

                    // 尝试从属性字段读取经纬度
                    double lon = geom.X; // 默认使用几何坐标
                    double lat = geom.Y;

                    // 尝试从属性字段读取经纬度
                    bool hasLonField = false;
                    bool hasLatField = false;

                    // 常见的经纬度字段名
                    string[] possibleLonFields = { "lon", "longitude", "x", "经度", "long" };
                    string[] possibleLatFields = { "lat", "latitude", "y", "纬度", "lati" };

                    foreach (var fieldName in possibleLonFields)
                    {
                        try
                        {
                            var value = reader[fieldName];
                            if (value != null && double.TryParse(value.ToString(), out double parsedLon))
                            {
                                lon = parsedLon;
                                hasLonField = true;
                                Console.WriteLine($"    从字段 '{fieldName}' 读取经度: {lon}");
                                break;
                            }
                        }
                        catch
                        {
                            // 字段不存在，继续尝试下一个
                        }
                    }

                    foreach (var fieldName in possibleLatFields)
                    {
                        try
                        {
                            var value = reader[fieldName];
                            if (value != null && double.TryParse(value.ToString(), out double parsedLat))
                            {
                                lat = parsedLat;
                                hasLatField = true;
                                Console.WriteLine($"    从字段 '{fieldName}' 读取纬度: {lat}");
                                break;
                            }
                        }
                        catch
                        {
                            // 字段不存在，继续尝试下一个
                        }
                    }

                    if (!hasLonField || !hasLatField)
                    {
                        Console.WriteLine($"  警告: 记录 {recordCount} 未找到经纬度字段，使用几何坐标");
                    }

                    Nodes.Add(new StreetNode
                    {
                        Id = id++,
                        Lon = lon,
                        Lat = lat,
                        ImagePath = null,
                        DistanceToImage = double.MaxValue
                    });

                    Console.WriteLine($"  记录 {recordCount}: 点({lon:F6}, {lat:F6})");
                }

                Console.WriteLine($"成功加载 {Nodes.Count} 个点要素");
                reader.Dispose();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载SHP文件失败: {ex.Message}");
                Console.WriteLine($"堆栈跟踪: {ex.StackTrace}");
            }
        }

      
        public void LoadLineShapefile(string shpPath)
        {
            try
            {
                // Lines.Clear(); // 如果需要清空旧数据请取消注释
                Console.WriteLine($"[1] 准备加载 SHP 文件: {shpPath}");

                if (!File.Exists(shpPath))
                {
                    Console.WriteLine("[错误] 文件不存在！");
                    return;
                }

                var reader = new ShapefileDataReader(shpPath, GeometryFactory.Default);
                int id = 0;
                int rawRecordCount = 0;
                int successCount = 0;

                while (reader.Read())
                {
                    rawRecordCount++;
                    var geometry = reader.Geometry; // 获取几何体

                    if (geometry == null) continue;

                    // --- 通用处理逻辑 ---
                    bool hasAdded = false;

                    // 遍历该几何体的所有组成部分
                    // GetGeometryN 和 NumGeometries 是所有几何体通用的方法
                    for (int i = 0; i < geometry.NumGeometries; i++)
                    {
                        var part = geometry.GetGeometryN(i);

                        // 检查坐标是否存在
                        if (part.Coordinates != null && part.Coordinates.Length > 1)
                        {
                            // ★★★ 核心修复：手动提取 X,Y ★★★
                            // 不管 part.Coordinates 是 GeoAPI 还是 NTS，
                            // 只要它能被 foreach 遍历且有 X,Y 属性，这段代码就能跑。
                            var pointList = new List<Coordinate>();

                            foreach (var rawPoint in part.Coordinates)
                            {
                                // 使用文件顶部定义的 using Coordinate = ... 来创建新点
                                pointList.Add(new Coordinate(rawPoint.X, rawPoint.Y));
                            }

                            Lines.Add(new StreetLine
                            {
                                Id = id++,
                                Coordinates = pointList.ToArray()
                            });

                            hasAdded = true;
                            successCount++;
                        }
                    }

                    // 调试日志
                    if (!hasAdded && rawRecordCount <= 5)
                    {
                        Console.WriteLine($"[警告] 记录 {rawRecordCount} 读取到了几何体，但未能提取出有效坐标。类型: {geometry.GetType().Name}");
                    }
                }

                reader.Dispose();
                Console.WriteLine($"[结束] 扫描到 {rawRecordCount} 条记录，成功加载 {successCount} 条线段");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[异常] 加载线SHP崩溃: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
        
        public void Pan(double screenDx, double screenDy, double canvasWidth, double canvasHeight)
        {
            if (!isViewInitialized) ResetView();

            var currentBounds = GetCurrentViewBounds();

            // 计算 屏幕像素 到 地理坐标 的比例
            // 例如：屏幕移动100像素，相当于地理坐标移动了多少度
            double ratioX = currentBounds.Width / canvasWidth;
            double ratioY = currentBounds.Height / canvasHeight;

           
            viewCenterX -= screenDx * ratioX;

           
            viewCenterY += screenDy * ratioY;
        }

        // -------------------------------
        //   自动绑定 lon_lat.jpg 格式的图片
        // -------------------------------
        public int AutoBindImages(string folder)
        {
            try
            {
                Console.WriteLine($"开始绑定图片，文件夹: {folder}");

                if (!Directory.Exists(folder))
                {
                    Console.WriteLine($"图片文件夹不存在: {folder}");
                    return 0;
                }

                // 1. 获取所有图片
                var files = Directory.GetFiles(folder, "*.jpg", SearchOption.AllDirectories);
                Console.WriteLine($"扫描到 {files.Length} 个JPG文件，正在解析坐标...");

                // 2. 预解析所有图片的文件名，转换为坐标
                // 结构：(经度, 纬度, 文件路径)
                var imageMetaList = new List<(double Lon, double Lat, string Path)>();

                foreach (var file in files)
                {
                    try
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);

                        // 假设文件名格式为: 114.38663_30.51497866
                        // 使用 '_' 分割
                        var parts = fileName.Split('_');

                        if (parts.Length >= 2)
                        {
                            // 尝试解析经纬度
                            if (double.TryParse(parts[0], out double imgLon) &&
                                double.TryParse(parts[1], out double imgLat))
                            {
                                imageMetaList.Add((imgLon, imgLat, file));
                            }
                        }
                    }
                    catch
                    {
                        // 忽略解析失败的文件名
                    }
                }

                Console.WriteLine($"成功解析出 {imageMetaList.Count} 张带有坐标信息的图片");

                // 3. 开始匹配
                int count = 0;
                // 设定容差：0.00001 度大约等于 1米左右的误差范围
                // 你的例子中相差 0.00000006，完全在这个范围内
                double tolerance = 0.00002;

                foreach (var node in Nodes)
                {
                    // 在所有图片中查找距离该节点最近，且距离小于容差的图片
                    // 这里使用简单的曼哈顿距离 (|dx| + |dy|) 计算，效率更高，对于微小距离足够准确

                    var bestMatch = imageMetaList
                        .Select(img => new
                        {
                            Info = img,
                            // 计算坐标差值绝对值之和
                            Diff = Math.Abs(img.Lon - node.Lon) + Math.Abs(img.Lat - node.Lat)
                        })
                        .Where(x => x.Diff < tolerance) // 筛选出在容差范围内的
                        .OrderBy(x => x.Diff)           // 按差异从小到大排序
                        .FirstOrDefault();              // 取最匹配的那个

                    if (bestMatch != null)
                    {
                        node.ImagePath = bestMatch.Info.Path;
                        node.DistanceToImage = bestMatch.Diff; // 这里的距离是坐标差值，仅供参考
                        count++;

                        // 调试输出（可选，数据多时建议注释掉）
                        // Console.WriteLine($"  节点{node.Id} 匹配到 {Path.GetFileName(node.ImagePath)} (误差: {bestMatch.Diff:F8})");
                    }
                }

                Console.WriteLine($"匹配完成，共成功绑定 {count} 张图片");
                return count;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"绑定图片流程出错: {ex.Message}");
                return 0;
            }
        }

        // 生成可能的文件名（支持不同精度）
        private string[] GeneratePossibleFileNames(double lon, double lat)
        {
            var fileNames = new List<string>();

            // 不同精度级别
            int[] precisions = { 6, 5, 4, 3, 2 };

            foreach (int precision in precisions)
            {
                string lonStr = lon.ToString($"F{precision}", CultureInfo.InvariantCulture);
                string latStr = lat.ToString($"F{precision}", CultureInfo.InvariantCulture);

                // 基本格式: lon_lat
                fileNames.Add($"{lonStr}_{latStr}");

                // 其他可能的格式
                fileNames.Add($"{lonStr.Replace(".", "_")}_{latStr.Replace(".", "_")}");
                fileNames.Add($"{lonStr.Replace(".", "")}_{latStr.Replace(".", "")}");
            }

            return fileNames.Distinct().ToArray();
        }

        // -------------------------------
        //   查找最近节点
        // -------------------------------
        public StreetNode FindNearestNode(double lon, double lat)
        {
            if (Nodes == null || Nodes.Count == 0)
            {
                Console.WriteLine("     节点列表为空");
                return null;
            }

            StreetNode best = null;
            double bestDist = double.MaxValue;

            foreach (var n in Nodes)
            {
                double d = Haversine(lat, lon, n.Lat, n.Lon);
                if (d < bestDist)
                {
                    best = n;
                    bestDist = d;
                }
            }

            if (best != null)
            {
                Console.WriteLine($"      找到最近节点: ID={best.Id}, 距离={bestDist:F2}米");
            }

            return best;
        }

        // -------------------------------
        //   经纬度距离（Haversine）
        // -------------------------------
        private static double Haversine(double lat1, double lon1, double lat2, double lon2)
        {
            double R = 6371000; // meters

            double dLat = (lat2 - lat1) * Math.PI / 180;
            double dLon = (lon2 - lon1) * Math.PI / 180;

            lat1 *= Math.PI / 180;
            lat2 *= Math.PI / 180;

            double a =
                Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(lat1) * Math.Cos(lat2) *
                Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }

        // -------------------------------
        //   地图渲染
        // -------------------------------
        public Bitmap RenderMap(List<StreetNode> nodes)
        {
            try
            {
                var bounds = GetMapBounds();
                int width = 800;
                int height = 300;

                var bitmap = new Bitmap(width, height);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    // 开启抗锯齿，线条更平滑
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.Clear(Color.LightGray);

                    DrawGrid(graphics, bounds, width, height);

                    graphics.Clear(Color.FromArgb(255, 248, 220));
                    DrawLines(graphics, Lines, bounds, width, height);
                    
                    DrawNodes(graphics, nodes, bounds, width, height);
                }
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"渲染失败: {ex.Message}");
                return new Bitmap(800, 300);
            }
        }
        private void DrawLines(Graphics graphics, List<StreetLine> lines, MapBounds bounds, int width, int height)
        {
            if (lines == null || lines.Count == 0) return;

            var allScreenLines = new List<DrawingPoint[]>();

            foreach (var line in lines)
            {
                if (line.Coordinates == null || line.Coordinates.Length < 2) continue;

                var points = new List<DrawingPoint>();
                foreach (var coord in line.Coordinates)
                {
                    int screenX = (int)((coord.X - bounds.MinX) / bounds.Width * width);
                    int screenY = height - (int)((coord.Y - bounds.MinY) / bounds.Height * height);
                    points.Add(new DrawingPoint(screenX, screenY));
                }
                allScreenLines.Add(points.ToArray());
            }

            using (var outerPen = new Pen(Color.Gray, 6))
            using (var innerPen = new Pen(Color.White, 4))
            {
                // 设置圆头和圆角连接
                // System.Drawing.Drawing2D 需要引用，或者写全名
                var roundCap = System.Drawing.Drawing2D.LineCap.Round;
                var roundJoin = System.Drawing.Drawing2D.LineJoin.Round;

                outerPen.StartCap = roundCap;
                outerPen.EndCap = roundCap;
                outerPen.LineJoin = roundJoin;

                innerPen.StartCap = roundCap;
                innerPen.EndCap = roundCap;
                innerPen.LineJoin = roundJoin;

                foreach (var points in allScreenLines)
                {
                    try { graphics.DrawLines(outerPen, points); } catch { }
                }
                foreach (var points in allScreenLines)
                {
                    try { graphics.DrawLines(innerPen, points); } catch { }
                }
            }
        }
        private void DrawGrid(Graphics graphics, MapBounds bounds, int width, int height)
        {
            using (var pen = new Pen(Color.DarkGray, 1))
            {
                // 绘制网格线
                double gridSize = CalculateGridSize(bounds);

                double startX = Math.Floor(bounds.MinX / gridSize) * gridSize;
                double startY = Math.Floor(bounds.MinY / gridSize) * gridSize;

                for (double x = startX; x <= bounds.MaxX; x += gridSize)
                {
                    int screenX = (int)((x - bounds.MinX) / bounds.Width * width);
                    graphics.DrawLine(pen, screenX, 0, screenX, height);
                }

                for (double y = startY; y <= bounds.MaxY; y += gridSize)
                {
                    int screenY = height - (int)((y - bounds.MinY) / bounds.Height * height);
                    graphics.DrawLine(pen, 0, screenY, width, screenY);
                }
            }
        }

        private void DrawNodes(Graphics graphics, List<StreetNode> nodes, MapBounds bounds, int width, int height)
        {
            if (nodes == null || nodes.Count == 0)
            {
                // 绘制提示信息
                //graphics.DrawString("没有数据点", new Font("Arial", 16), Brushes.Red, width / 2 - 50, height / 2 - 10);
                return;
            }

            using (var noImageBrush = new SolidBrush(Color.Gray))
            using (var hasImageBrush = new SolidBrush(Color.Red))
            using (var outlinePen = new Pen(Color.Black, 1))
            {
                int nodesWithImages = 0;

                foreach (var node in nodes)
                {
                    int screenX = (int)((node.Lon - bounds.MinX) / bounds.Width * width);
                    int screenY = height - (int)((node.Lat - bounds.MinY) / bounds.Height * height);

                    // 根据是否有图片选择颜色
                    var brush = string.IsNullOrEmpty(node.ImagePath) ? noImageBrush : hasImageBrush;

                    // 绘制点位
                    graphics.FillEllipse(brush, screenX - 4, screenY - 4, 4, 4);
                    graphics.DrawEllipse(outlinePen, screenX - 4, screenY - 4, 4, 4);

                    // 绘制编号（只绘制有图片的节点）
                    if (!string.IsNullOrEmpty(node.ImagePath))
                    {
                        graphics.DrawString(
                            (node.Id + 1).ToString(),
                            new Font("Arial", 8),
                            Brushes.White,
                            screenX + 6,
                            screenY - 6
                        );
                        nodesWithImages++;
                    }
                }

                // 绘制统计信息
                graphics.DrawString(
                    $"总节点: {nodes.Count}, 有图片: {nodesWithImages}",
                    new Font("Arial", 10),
                    Brushes.Black,
                    10, 10
                );
            }
        }

        private double CalculateGridSize(MapBounds bounds)
        {
            double range = Math.Max(bounds.Width, bounds.Height);
            if (range == 0) return 10;

            double baseSize = Math.Pow(10, Math.Floor(Math.Log10(range)));

            if (range / baseSize > 5) return baseSize * 2;
            if (range / baseSize > 2) return baseSize;
            return baseSize / 2;
        }
        public MapBounds GetCurrentViewBounds()
        {
            var fullBounds = GetDataBounds();
            if (!isViewInitialized) ResetView();

            // 根据缩放级别计算当前的视野宽高
            double currentGeoWidth = fullBounds.Width / zoomLevel;
            double currentGeoHeight = fullBounds.Height / zoomLevel;

            // 基于当前中心点计算边界
            return new MapBounds(
                viewCenterX - currentGeoWidth / 2,
                viewCenterY - currentGeoHeight / 2,
                viewCenterX + currentGeoWidth / 2,
                viewCenterY + currentGeoHeight / 2
            );
        }
        public MapBounds GetDataBounds()
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            bool hasData = false;

            // 1. 统计点数据范围
            if (Nodes != null && Nodes.Count > 0)
            {
                foreach (var node in Nodes)
                {
                    if (node.Lon < minX) minX = node.Lon;
                    if (node.Lon > maxX) maxX = node.Lon;
                    if (node.Lat < minY) minY = node.Lat;
                    if (node.Lat > maxY) maxY = node.Lat;
                }
                hasData = true;
            }

            // 2. ★★★ 必须添加：统计线数据范围 ★★★
            if (Lines != null && Lines.Count > 0)
            {
                foreach (var line in Lines)
                {
                    if (line.Coordinates == null) continue;
                    foreach (var coord in line.Coordinates)
                    {
                        if (coord.X < minX) minX = coord.X;
                        if (coord.X > maxX) maxX = coord.X;
                        if (coord.Y < minY) minY = coord.Y;
                        if (coord.Y > maxY) maxY = coord.Y;
                    }
                }
                hasData = true;
            }

            // 如果完全没有数据
            if (!hasData) return new MapBounds(0, 0, 100, 100);

            // 计算一点边距，让数据不要顶格显示
            double width = maxX - minX;
            double height = maxY - minY;

            // 防止由单点导致宽高为0
            if (width == 0) width = 0.01;
            if (height == 0) height = 0.01;

            double marginX = width * 0.1;
            double marginY = height * 0.1;

            return new MapBounds(minX - marginX, minY - marginY, maxX + marginX, maxY + marginY);
        }

        // 2. 修改 RenderMap 方法，接收外部传入的宽高，并使用当前视图范围
        // 修改前: public Bitmap RenderMap(List<StreetNode> nodes)
        public Bitmap RenderMap(List<StreetNode> nodes, int canvasWidth, int canvasHeight)
        {
            try
            {
                // ★★★ 关键修改：获取缩放后的范围，而不是全图范围 ★★★
                var bounds = GetCurrentViewBounds();

                // 防止除以0
                if (canvasWidth <= 0) canvasWidth = 1;
                if (canvasHeight <= 0) canvasHeight = 1;

                var bitmap = new Bitmap(canvasWidth, canvasHeight);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    graphics.Clear(Color.LightGray); // 背景色

                    // 传入当前的 bounds 和 画布尺寸
                    DrawGrid(graphics, bounds, canvasWidth, canvasHeight);

                    graphics.Clear(Color.FromArgb(255, 248, 220)); // 街道背景
                    DrawLines(graphics, Lines, bounds, canvasWidth, canvasHeight);

                    DrawNodes(graphics, nodes, bounds, canvasWidth, canvasHeight);
                }
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"渲染失败: {ex.Message}");
                return new Bitmap(Math.Max(1, canvasWidth), Math.Max(1, canvasHeight));
            }
        }
        public MapBounds GetMapBounds()
        {
            double minX = double.MaxValue, maxX = double.MinValue;
            double minY = double.MaxValue, maxY = double.MinValue;
            bool hasData = false;

            // 1. 统计点数据范围
            if (Nodes != null && Nodes.Count > 0)
            {
                minX = Math.Min(minX, Nodes.Min(n => n.Lon));
                maxX = Math.Max(maxX, Nodes.Max(n => n.Lon));
                minY = Math.Min(minY, Nodes.Min(n => n.Lat));
                maxY = Math.Max(maxY, Nodes.Max(n => n.Lat));
                hasData = true;
            }

            // 2. 统计线数据范围
            if (Lines != null && Lines.Count > 0)
            {
                foreach (var line in Lines)
                {
                    foreach (var coord in line.Coordinates)
                    {
                        minX = Math.Min(minX, coord.X);
                        maxX = Math.Max(maxX, coord.X);
                        minY = Math.Min(minY, coord.Y);
                        maxY = Math.Max(maxY, coord.Y);
                    }
                }
                hasData = true;
            }

            if (!hasData) return new MapBounds(0, 0, 100, 100);

            // 计算边距
            double marginX = (maxX - minX) * 0.1;
            double marginY = (maxY - minY) * 0.1;
            double margin = Math.Max(Math.Max(marginX, marginY), 0.001);

            return new MapBounds(minX - margin, minY - margin, maxX + margin, maxY + margin);
        }
        public class MapBounds
        {
            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
            public double Width => MaxX - MinX;
            public double Height => MaxY - MinY;

            public MapBounds(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }
        }

        public class CoordinateTransformer
        {
            private MapBounds bounds;
            private System.Windows.Size canvasSize;

            public CoordinateTransformer(MapBounds mapBounds, System.Windows.Size size)
            {
                bounds = mapBounds;
                canvasSize = size;
            }

            public System.Windows.Point GeoToScreen(double x, double y)
            {
                double xRatio = (x - bounds.MinX) / bounds.Width;
                double yRatio = 1.0 - (y - bounds.MinY) / bounds.Height; // Y轴翻转

                return new System.Windows.Point(
                    xRatio * canvasSize.Width,
                    yRatio * canvasSize.Height
                );
            }

            public (double x, double y) ScreenToGeo(System.Windows.Point screenPoint)
            {
                double xRatio = screenPoint.X / canvasSize.Width;
                double yRatio = 1.0 - (screenPoint.Y / canvasSize.Height); // Y轴翻转

                double x = bounds.MinX + xRatio * bounds.Width;
                double y = bounds.MinY + yRatio * bounds.Height;

                return (x, y);
            }
        }
        public void ZoomIn()
        {
            zoomLevel *= 1.2;
        }

        public void ZoomOut()
        {
            zoomLevel /= 1.2;
            if (zoomLevel < 0.1) zoomLevel = 0.1;
        }

        public void ResetView()
        {
            var fullBounds = GetDataBounds(); // 获取所有数据的最大范围
            viewCenterX = (fullBounds.MinX + fullBounds.MaxX) / 2;
            viewCenterY = (fullBounds.MinY + fullBounds.MaxY) / 2;
            zoomLevel = 1.0;
            isViewInitialized = true;
        }

    }
}