using Microsoft.AspNetCore.Mvc;
using ArtGallery.Models;
using ArtGallery.Repositories.Interfaces;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ArtGallery.Models.VNPAY;
using System.Text.RegularExpressions;

namespace ArtGallery.Controllers
{
    [Authorize]
    public class OrderController : Controller
    {
        private readonly IOrderRepository _orderRepository;
        private readonly ILogger<OrderController> _logger;
        private readonly UserManager<NguoiDung> _userManager;
        private readonly ArtGalleryContext _context;
        private readonly INotificationRepository _notificationRepository;
        private readonly EmailService _emailService;
        private string ExtractEmailFromOrderInfo(string orderInfo)
        {
            var match = Regex.Match(orderInfo, @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}");
            return match.Success ? match.Value : string.Empty;
        }

        public OrderController(
            IOrderRepository orderRepository, 
            ILogger<OrderController> logger, 
            UserManager<NguoiDung> userManager, 
            ArtGalleryContext context,
            INotificationRepository notificationRepository,
            EmailService emailService)
        {
            _orderRepository = orderRepository;
            _logger = logger;
            _userManager = userManager;
            _context = context;
            _notificationRepository = notificationRepository;
            _emailService = emailService;
        }

        public async Task<IActionResult> Display(int id, int? orderId = null)
        {
            // Lấy thông tin tranh từ cơ sở dữ liệu
            var artwork = await _context.Tranhs
                .Include(t => t.MaNguoiDungNavigation)
                .FirstOrDefaultAsync(t => t.MaTranh == id);

            if (artwork == null)
            {
                return NotFound();
            }

            // Kiểm tra thông tin của nghệ sĩ (người bán)
            var artistUser = await _context.NguoiDungs.FirstOrDefaultAsync(u => u.Id == artwork.MaNguoiDung);
            if (artistUser != null)
            {
                ViewBag.IsArtistLocked = artistUser.LockoutEnabled && artistUser.LockoutEnd != null && artistUser.LockoutEnd > DateTimeOffset.UtcNow;
                ViewBag.ArtistLockoutEnd = artistUser.LockoutEnd;
                ViewBag.ArtistLockoutReason = artistUser.LockoutReason;
            }

            // Gán giá trị cho ViewBag
            ViewBag.Artwork = artwork;

            // Nếu có orderId, lấy thông tin đơn hàng
            if (orderId.HasValue)
            {
                var userId = _userManager.GetUserId(User);
                var existingOrder = await _context.GiaoDiches
                    .FirstOrDefaultAsync(g => g.MaGiaoDich == orderId.Value && g.MaNguoiMua == userId);

                if (existingOrder != null)
                {
                    ViewBag.ExistingOrder = existingOrder;
                }
            }

            return View(artwork);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(int artworkId, int quantity, decimal totalAmount)
        {
            if (quantity <= 0)
            {
                TempData["ErrorMessage"] = "Số lượng phải lớn hơn 0";
                return RedirectToAction("Display", new { id = artworkId });
            }

            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var artwork = await _orderRepository.GetArtworkById(artworkId);

            if (artwork == null)
            {
                TempData["ErrorMessage"] = "Không tìm thấy tranh";
                return RedirectToAction("Index", "Home");
            }

            // Tính lại tổng tiền để kiểm tra
            var calculatedTotal = artwork.Gia * quantity;

            var giaoDich = new GiaoDich
            {
                MaNguoiMua = currentUserId,
                MaTranh = artworkId,
                SoLuong = quantity,
                SoTien = calculatedTotal // Sử dụng giá trị tính toán từ server để đảm bảo tính chính xác
            };

            var result = await _orderRepository.CreateOrder(giaoDich);

            if (result.success)
            {
                TempData["SuccessMessage"] = result.message;
                return RedirectToAction("OrderSuccess", new { id = giaoDich.MaGiaoDich });
            }
            else
            {
                TempData["ErrorMessage"] = result.message;
                return RedirectToAction("Display", new { id = artworkId });
            }
        }

        [HttpPost]
        public async Task<IActionResult> PlaceOrder(int maTranh, int soLuong, decimal tongTien, string trangThai, string phuongThucThanhToan, int? orderId = null)
        {
            try
            {
                // Lấy thông tin người dùng hiện tại
                var userId = _userManager.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                {
                    return Json(new { success = false, message = "Bạn cần đăng nhập để đặt hàng" });
                }

                // Lấy thông tin tranh và người bán
                var artwork = await _context.Tranhs
                    .Include(t => t.MaNguoiDungNavigation)
                    .FirstOrDefaultAsync(t => t.MaTranh == maTranh);
                
                if (artwork == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy tác phẩm" });
                }

                // Nếu có orderId (đang xác nhận đơn hàng từ giỏ hàng), cập nhật giao dịch hiện có
                if (orderId.HasValue)
                {
                    var existingOrder = await _context.GiaoDiches.FindAsync(orderId.Value);
                    if (existingOrder != null && existingOrder.MaNguoiMua == userId)
                    {
                        // Cập nhật trạng thái và phương thức thanh toán
                        existingOrder.TrangThai = trangThai;
                        existingOrder.PhuongThucThanhToan = phuongThucThanhToan;
                        
                        _context.GiaoDiches.Update(existingOrder);
                        await _context.SaveChangesAsync();
                        
                        // Gửi thông báo cho người bán
                        // Đối với đơn hàng mới (Đã đặt hàng), cần thông báo để người bán xác nhận
                            var buyer = await _userManager.FindByIdAsync(userId);
                            var sellerId = artwork.MaNguoiDung;
                            
                            // Tạo thông báo cho người bán
                            await _notificationRepository.CreateNotification(
                                receiverId: sellerId,
                                senderId: userId,
                                title: "Đơn hàng mới",
                                content: $"{buyer.TenNguoiDung} đã đặt mua tác phẩm {artwork.TieuDe} với số lượng {soLuong}",
                                url: "/Order/History",
                                notificationType: "order",
                                imageUrl: artwork.DuongDanAnh
                            );
                        
                        return Json(new { success = true });
                    }
                    
                    return Json(new { success = false, message = "Không tìm thấy đơn hàng cần cập nhật" });
                }
                else
                {
                    // Tạo giao dịch mới 
                    var giaoDich = new GiaoDich
                    {
                        MaNguoiMua = userId,
                        MaTranh = maTranh,
                        SoLuong = soLuong,
                        SoTien = tongTien,
                        NgayMua = DateTime.Now,
                        TrangThai = trangThai, // Trạng thái sẽ là "Đã đặt hàng"
                        PhuongThucThanhToan = phuongThucThanhToan
                    };
                    
                    // Thêm vào cơ sở dữ liệu
                    _context.GiaoDiches.Add(giaoDich);
                    
                    // Cập nhật số lượng tranh còn lại
                    if (artwork != null)
                    {
                        artwork.SoLuongTon -= soLuong;
                        _context.Tranhs.Update(artwork);
                    }
                    
                    await _context.SaveChangesAsync();
                    
                    // Gửi thông báo cho người bán khi đặt hàng mới
                        var buyer = await _userManager.FindByIdAsync(userId);
                        var sellerId = artwork.MaNguoiDung;
                        
                        // Tạo thông báo cho người bán
                        await _notificationRepository.CreateNotification(
                            receiverId: sellerId,
                            senderId: userId,
                            title: "Đơn hàng mới",
                            content: $"{buyer.TenNguoiDung} đã đặt mua tác phẩm {artwork.TieuDe} với số lượng {soLuong}",
                            url: "/Order/History",
                            notificationType: "order",
                            imageUrl: artwork.DuongDanAnh
                        );
                    
                    return Json(new { success = true });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        // Action hiển thị trang đặt hàng thành công
        [HttpGet]
        public async Task<IActionResult> OrderSuccess()
        {
            try
            {
                var vnp_ResponseCode = Request.Query["vnp_ResponseCode"].ToString();
                var vnp_TxnRef = Request.Query["vnp_TxnRef"].ToString();
                var vnp_Amount = Request.Query["vnp_Amount"].ToString();
                var vnp_OrderInfo = Request.Query["vnp_OrderInfo"].ToString();
                var vnp_TransactionNo = Request.Query["vnp_TransactionNo"].ToString();
                var vnp_BankCode = Request.Query["vnp_BankCode"].ToString();
                var vnp_CardType = Request.Query["vnp_CardType"].ToString();

                // Nếu thanh toán thành công (00 là thành công)
                if (vnp_ResponseCode == "00")
                {
                    int artworkId = 0, quantity = 0, orderId = 0;

                    var parts = vnp_OrderInfo.Split('+');
                    if (parts.Length >= 4)
                    {
                        int.TryParse(parts[1], out orderId);
                        int.TryParse(parts[2], out artworkId);
                        int.TryParse(parts[3], out quantity);
                    }

                    if (artworkId <= 0 || quantity <= 0)
                    {
                        var form = HttpContext.Session.GetString("VNPayFormData");
                        if (!string.IsNullOrEmpty(form))
                        {
                            var formData = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(form);
                            int.TryParse(formData.GetValueOrDefault("MaTranh"), out artworkId);
                            int.TryParse(formData.GetValueOrDefault("SoLuong"), out quantity);
                            int.TryParse(formData.GetValueOrDefault("OrderId"), out orderId);
                        }
                    }

                    if (artworkId > 0 && quantity > 0)
                    {
                        var userId = _userManager.GetUserId(User);
                        var artwork = await _context.Tranhs.Include(t => t.MaNguoiDungNavigation).FirstOrDefaultAsync(t => t.MaTranh == artworkId);

                        if (artwork != null)
                        {
                            decimal amount = decimal.Parse(vnp_Amount) / 100;

                            GiaoDich giaoDich;
                            if (orderId > 0)
                            {
                                giaoDich = await _context.GiaoDiches.FindAsync(orderId);
                                if (giaoDich != null && giaoDich.MaNguoiMua == userId)
                                {
                                    giaoDich.TrangThai = "Đã đặt hàng";
                                    giaoDich.PhuongThucThanhToan = "VNPAY";
                                    giaoDich.SoLuong = quantity;
                                    giaoDich.SoTien = amount;
                                    giaoDich.NgayMua = DateTime.Now;

                                    _context.GiaoDiches.Update(giaoDich);
                                }
                                else
                                {
                                    giaoDich = new GiaoDich
                                    {
                                        MaNguoiMua = userId,
                                        MaTranh = artworkId,
                                        SoLuong = quantity,
                                        SoTien = amount,
                                        NgayMua = DateTime.Now,
                                        TrangThai = "Đã đặt hàng",
                                        PhuongThucThanhToan = "VNPAY"
                                    };
                                    _context.GiaoDiches.Add(giaoDich);
                                }
                            }
                            else
                            {
                                giaoDich = new GiaoDich
                                {
                                    MaNguoiMua = userId,
                                    MaTranh = artworkId,
                                    SoLuong = quantity,
                                    SoTien = amount,
                                    NgayMua = DateTime.Now,
                                    TrangThai = "Đã đặt hàng",
                                    PhuongThucThanhToan = "VNPAY"
                                };
                                _context.GiaoDiches.Add(giaoDich);
                            }

                            artwork.SoLuongTon -= quantity;
                            _context.Tranhs.Update(artwork);

                            await _context.SaveChangesAsync();

                            // Gửi email xác nhận
                            var user = await _userManager.FindByIdAsync(userId);
                            if (user != null)
                            {
                                string orderInfo = $@"
                            <p>Mã giao dịch: {vnp_TxnRef}</p>
                            <p>Sản phẩm: {artwork.TieuDe}</p>
                            <p>Số lượng: {quantity}</p>
                            <p>Thành tiền: {amount:N0} VND</p>
                            <p>Phương thức thanh toán: VNPAY</p>";

                                await _emailService.SendOrderConfirmationEmail(
                                    user.Email,
                                    user.TenNguoiDung,
                                    orderInfo
                                );
                            }
                        }
            }

            var paymentResponse = new PaymentResponseModel
            {
                OrderId = vnp_TxnRef,
                TransactionId = vnp_TransactionNo,
                OrderDescription = vnp_OrderInfo,
                VnPayResponseCode = vnp_ResponseCode,
                        PaymentMethod = "VNPAY",
                        BankCode = vnp_BankCode,
                        CardType = vnp_CardType
            };

            return View(paymentResponse);
        }
                else
                {
                    // Gửi email báo thanh toán thất bại
                    var user = await _userManager.GetUserAsync(User);
                    if (user != null)
                    {
                        string failInfo = $@"
                    <p>Mã đơn hàng: {vnp_TxnRef}</p>
                    <p>Thông tin: {vnp_OrderInfo}</p>
                    <p>Trạng thái: <strong>Thất bại</strong></p>";

                        await _emailService.SendOrderConfirmationEmail(
                            user.Email,
                            user.TenNguoiDung,
                            failInfo
                        );
                    }

                    return RedirectToAction("PaymentError");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error in OrderSuccess: {ex.Message}");
                return View(new PaymentResponseModel
                {
                    VnPayResponseCode = "99",
                    OrderDescription = "Lỗi xử lý giao dịch"
                });
            }
        }



        // Thêm action PaymentError nếu cần
        [HttpGet]
        public IActionResult PaymentError()
        {
            return View();
        }

        public async Task<IActionResult> History()
        {
            var currentUserId = User.FindFirstValue(ClaimTypes.NameIdentifier);

            // Lấy đơn hàng mua của người dùng (không bao gồm những đơn hàng đã ẩn)
            var buyOrders = await _context.GiaoDiches
                .Include(g => g.MaTranhNavigation)
                .Include(g => g.MaNguoiMuaNavigation)
                .Where(g => g.MaNguoiMua == currentUserId && (g.IsHiddenByBuyer == false || g.IsHiddenByBuyer == null))
                .ToListAsync();

            // Lấy đơn hàng bán (người dùng là người tạo ra tranh)
            var sellOrders = await _context.GiaoDiches
                .Include(g => g.MaTranhNavigation)
                .Include(g => g.MaNguoiMuaNavigation)
                .Where(g => g.MaTranhNavigation.MaNguoiDung == currentUserId && 
                       g.MaNguoiMua != currentUserId && 
                       (g.IsHiddenBySeller == false || g.IsHiddenBySeller == null))
                .ToListAsync();

            // Kết hợp danh sách
            var allOrders = buyOrders.Concat(sellOrders).ToList();

            return View(allOrders);
        }

        [HttpPost]
        public async Task<IActionResult> AddToCart(int maTranh, int soLuong, decimal tongTien, string trangThai, string phuongThucThanhToan)
        {
            try
            {
                // Lấy thông tin người dùng hiện tại
                var userId = _userManager.GetUserId(User);
                if (string.IsNullOrEmpty(userId))
                {
                    return Json(new { success = false, message = "Bạn cần đăng nhập để thêm vào giỏ hàng" });
                }

                // Lấy thông tin tranh và người bán
                var artwork = await _context.Tranhs
                    .Include(t => t.MaNguoiDungNavigation)
                    .FirstOrDefaultAsync(t => t.MaTranh == maTranh);
                
                if (artwork == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy tác phẩm" });
                }

                // Tạo giao dịch mới với trạng thái "Chờ xác nhận"
                var giaoDich = new GiaoDich
                {
                    MaNguoiMua = userId,
                    MaTranh = maTranh,
                    SoLuong = soLuong,
                    SoTien = tongTien,
                    NgayMua = DateTime.Now,
                    TrangThai = trangThai,
                    PhuongThucThanhToan = phuongThucThanhToan
                };

                // Thêm vào cơ sở dữ liệu
                _context.GiaoDiches.Add(giaoDich);
                await _context.SaveChangesAsync();

                // Gửi thông báo cho người bán khi có người thêm sản phẩm vào giỏ hàng
                var buyer = await _userManager.FindByIdAsync(userId);
                var sellerId = artwork.MaNguoiDung;
                
                await _notificationRepository.CreateNotification(
                    receiverId: sellerId,
                    senderId: userId,
                    title: "Sản phẩm được thêm vào giỏ hàng",
                    content: $"{buyer.TenNguoiDung} đã thêm tác phẩm {artwork.TieuDe} vào giỏ hàng",
                    url: "/Order/History",
                    notificationType: "cart",
                    imageUrl: artwork.DuongDanAnh
                );

                return Json(new { success = true, message = "Đã thêm vào giỏ hàng thành công" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        [HttpPost]
        public async Task<IActionResult> RemoveFromCart(int orderId)
        {
            try
            {
                var userId = _userManager.GetUserId(User);
                var order = await _context.GiaoDiches
                    .FirstOrDefaultAsync(g => g.MaGiaoDich == orderId && g.MaNguoiMua == userId);

                if (order == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy đơn hàng" });
                }

                if (order.TrangThai != "Chờ xác nhận")
                {
                    return Json(new { success = false, message = "Chỉ có thể xóa đơn hàng đang chờ xác nhận" });
                }

                // Xóa đơn hàng
                _context.GiaoDiches.Remove(order);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Đã xóa sản phẩm khỏi giỏ hàng" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> UpdateOrderStatus(int orderId, string status)
        {
            try
            {
                var userId = _userManager.GetUserId(User);
                var order = await _context.GiaoDiches
                    .Include(g => g.MaTranhNavigation)
                    .FirstOrDefaultAsync(g => g.MaGiaoDich == orderId);

                if (order == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy đơn hàng" });
                }

                // Kiểm tra quyền cập nhật (cho phép cả người bán và người mua hủy đơn)
                bool hasPermission = false;
                
                // Người bán có quyền thực hiện mọi hành động
                if (order.MaTranhNavigation.MaNguoiDung == userId)
                {
                    hasPermission = true;
                }
                // Người mua chỉ có quyền hủy đơn hàng của mình và đơn đó phải ở trạng thái "Đã đặt hàng"
                else if (order.MaNguoiMua == userId && status == "Đã hủy" && order.TrangThai == "Đã đặt hàng")
                {
                    hasPermission = true;
                }
                // Thêm điều kiện cho người mua xác nhận đã nhận hàng
                else if (order.MaNguoiMua == userId && status == "Đã hoàn thành" && order.TrangThai == "Đã xác nhận")
                {
                    hasPermission = true;
                }

                if (!hasPermission)
                {
                    return Json(new { success = false, message = "Bạn không có quyền cập nhật đơn hàng này" });
                }

                // Cập nhật trạng thái
                order.TrangThai = status;
                if (status == "Đã hoàn thành")
                {
                    // Cập nhật doanh thu cho người bán
                    var sellerId = order.MaTranhNavigation.MaNguoiDung;
                    var doanhThu = await _context.DoanhThus.FirstOrDefaultAsync(d => d.MaNguoiDung == sellerId);

                    if (doanhThu == null)
                    {
                        // Tạo mới nếu chưa có
                        doanhThu = new DoanhThu
                        {
                            MaNguoiDung = sellerId,
                            TongDoanhThu = order.SoTien,
                            SoTranhBanDuoc = order.SoLuong
                        };
                        _context.DoanhThus.Add(doanhThu);
                    }
                    else
                    {
                        // Cập nhật nếu đã có
                        doanhThu.TongDoanhThu += order.SoTien;
                        doanhThu.SoTranhBanDuoc += order.SoLuong;
                        _context.DoanhThus.Update(doanhThu);
                    }
                    
                    // THÊM ĐOẠN NÀY: Cập nhật số lượng đã bán trong bảng Tranh
                    var artwork = order.MaTranhNavigation;
                    artwork.DaBan += order.SoLuong;
                    _context.Tranhs.Update(artwork);
                }
                _context.GiaoDiches.Update(order);
                await _context.SaveChangesAsync();

                // Gửi thông báo cho người liên quan
                if (status == "Đã hủy")
                {
                    // Nếu người mua hủy, thông báo cho người bán
                    if (order.MaNguoiMua == userId)
                    {
                        var buyer = await _userManager.FindByIdAsync(userId);
                        await _notificationRepository.CreateNotification(
                            receiverId: order.MaTranhNavigation.MaNguoiDung,
                            senderId: userId,
                            title: "Đơn hàng đã bị hủy",
                            content: $"Người mua {buyer.TenNguoiDung} đã hủy đơn hàng #{order.MaGiaoDich}",
                            url: "/Order/History",
                            notificationType: "order",
                            imageUrl: order.MaTranhNavigation.DuongDanAnh
                        );
                    }
                    // Nếu người bán hủy, thông báo cho người mua
                    else
                    {
                        var seller = await _userManager.FindByIdAsync(userId);
                        await _notificationRepository.CreateNotification(
                            receiverId: order.MaNguoiMua,
                            senderId: userId,
                            title: "Đơn hàng đã bị hủy",
                            content: $"Người bán {seller.TenNguoiDung} đã hủy đơn hàng #{order.MaGiaoDich}",
                            url: "/Order/History",
                            notificationType: "order",
                            imageUrl: order.MaTranhNavigation.DuongDanAnh
                        );
                    }
                }
                else
                {
                    // Các thông báo khác giữ nguyên như cũ
                var seller = await _userManager.FindByIdAsync(userId);
                await _notificationRepository.CreateNotification(
                    receiverId: order.MaNguoiMua,
                    senderId: userId,
                    title: "Cập nhật đơn hàng",
                    content: $"Đơn hàng #{order.MaGiaoDich} đã được cập nhật sang trạng thái {status}",
                    url: "/Order/History",
                    notificationType: "order",
                    imageUrl: order.MaTranhNavigation.DuongDanAnh
                );
                }

                return Json(new { success = true, message = $"Đã cập nhật trạng thái đơn hàng thành {status}" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteHistory(int orderId)
        {
            try
            {
                var userId = _userManager.GetUserId(User);
                var order = await _context.GiaoDiches
                    .FirstOrDefaultAsync(g => g.MaGiaoDich == orderId && g.MaNguoiMua == userId);

                if (order == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy đơn hàng" });
                }

                // Chỉ cho phép ẩn lịch sử đơn hàng đã hoàn thành hoặc đã hủy
                if (order.TrangThai != "Đã hoàn thành" && order.TrangThai != "Đã hủy")
                {
                    return Json(new { success = false, message = "Chỉ có thể ẩn đơn hàng đã hoàn thành hoặc đã hủy" });
                }

                // Thay vì xóa đơn hàng, chỉ đánh dấu là đã ẩn
                order.IsHiddenByBuyer = true;
                _context.GiaoDiches.Update(order);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Đã ẩn đơn hàng khỏi lịch sử" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> HideSellerHistory(int orderId)
        {
            try
            {
                var userId = _userManager.GetUserId(User);
                // Tìm đơn hàng mà người dùng hiện tại là người bán
                var order = await _context.GiaoDiches
                    .Include(g => g.MaTranhNavigation)
                    .FirstOrDefaultAsync(g => g.MaGiaoDich == orderId && g.MaTranhNavigation.MaNguoiDung == userId);

                if (order == null)
                {
                    return Json(new { success = false, message = "Không tìm thấy đơn hàng" });
                }

                // Chỉ cho phép ẩn lịch sử đơn hàng đã hoàn thành hoặc đã hủy
                if (order.TrangThai != "Đã hoàn thành" && order.TrangThai != "Đã hủy")
                {
                    return Json(new { success = false, message = "Chỉ có thể ẩn đơn hàng đã hoàn thành hoặc đã hủy" });
                }

                // Đánh dấu là đã ẩn bởi người bán
                order.IsHiddenBySeller = true;
                _context.GiaoDiches.Update(order);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Đã ẩn đơn hàng khỏi lịch sử" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteAllHistory()
        {
            try
            {
                var userId = _userManager.GetUserId(User);
                
                // Tìm tất cả đơn hàng đã hoàn thành hoặc đã hủy mà người dùng là người mua
                var completedOrCancelledOrders = await _context.GiaoDiches
                    .Where(g => g.MaNguoiMua == userId && 
                          (g.TrangThai == "Đã hoàn thành" || g.TrangThai == "Đã hủy") &&
                          (g.IsHiddenByBuyer == false || g.IsHiddenByBuyer == null))
                    .ToListAsync();

                if (!completedOrCancelledOrders.Any())
                {
                    return Json(new { success = false, message = "Không có đơn hàng nào để xóa" });
                }

                // Đánh dấu tất cả là đã ẩn
                foreach (var order in completedOrCancelledOrders)
                {
                    order.IsHiddenByBuyer = true;
                }

                _context.GiaoDiches.UpdateRange(completedOrCancelledOrders);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Đã xóa tất cả lịch sử đơn hàng", count = completedOrCancelledOrders.Count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteAllSellerHistory()
        {
            try
            {
                var userId = _userManager.GetUserId(User);
                
                // Tìm tất cả đơn hàng đã hoàn thành hoặc đã hủy mà người dùng là người bán
                var completedOrCancelledOrders = await _context.GiaoDiches
                    .Include(g => g.MaTranhNavigation)
                    .Where(g => g.MaTranhNavigation.MaNguoiDung == userId &&
                          g.MaNguoiMua != userId &&
                          (g.TrangThai == "Đã hoàn thành" || g.TrangThai == "Đã hủy") &&
                          (g.IsHiddenBySeller == false || g.IsHiddenBySeller == null))
                    .ToListAsync();

                if (!completedOrCancelledOrders.Any())
                {
                    return Json(new { success = false, message = "Không có đơn hàng nào để xóa" });
                }

                // Đánh dấu tất cả là đã ẩn
                foreach (var order in completedOrCancelledOrders)
                {
                    order.IsHiddenBySeller = true;
                }

                _context.GiaoDiches.UpdateRange(completedOrCancelledOrders);
                await _context.SaveChangesAsync();

                return Json(new { success = true, message = "Đã xóa tất cả lịch sử bán hàng", count = completedOrCancelledOrders.Count });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Lỗi: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> OrderCOD(string DiaChi, string PhoneNumber)
        {
            var email = User.FindFirstValue(ClaimTypes.Email);
            if (string.IsNullOrEmpty(email))
            {
                TempData["ErrorMessage"] = "Không xác định được người dùng.";
                return RedirectToAction("OrderCOD");
            }

            var user = _context.NguoiDungs.FirstOrDefault(u => u.Email == email);
            if (user != null)
            {
                user.DiaChi = DiaChi;
                user.PhoneNumber = PhoneNumber;
                _context.SaveChanges();

                string orderInfo = $@"
            <p>Đơn hàng thanh toán khi nhận (COD):</p>
            <p><strong>Họ tên:</strong> {user.TenNguoiDung}</p>
            <p><strong>Email:</strong> {user.Email}</p>
            <p><strong>Địa chỉ:</strong> {DiaChi}</p>
            <p><strong>Số điện thoại:</strong> {PhoneNumber}</p>
        ";

                try
                {
                    await _emailService.SendOrderConfirmationEmail(user.Email, user.TenNguoiDung, orderInfo);
                    TempData["SuccessMessage"] = "Đơn hàng của bạn đã được xác nhận! Email đã được gửi.";
                }
                catch (Exception ex)
                {
                    TempData["SuccessMessage"] = "Đơn hàng xác nhận thành công nhưng gửi email thất bại.";
                    Console.WriteLine("Email error: " + ex.Message);
                }

            TempData["DiaChi"] = DiaChi;
            TempData["PhoneNumber"] = PhoneNumber;
            }
            else
            {
                TempData["ErrorMessage"] = "Không tìm thấy người dùng.";
            }

            return RedirectToAction("OrderCOD");
        }

        [HttpGet]
        public IActionResult OrderCOD()
        {
            ViewBag.DiaChi = TempData["DiaChi"];
            ViewBag.PhoneNumber = TempData["PhoneNumber"];
            ViewBag.Message = TempData["SuccessMessage"];
            ViewBag.ErrorMessage = TempData["ErrorMessage"];
            return View(); // OrderCOD.cshtml
        }

        [HttpGet]
        public async Task<IActionResult> GetOrderDetail(int id)
        {
            try
            {
                var order = await _context.GiaoDiches
                    .Include(g => g.MaNguoiMuaNavigation)
                    .Include(g => g.MaTranhNavigation)
                        .ThenInclude(t => t.MaNguoiDungNavigation)
                    .FirstOrDefaultAsync(g => g.MaGiaoDich == id);
                
                if (order == null)
                    return NotFound(new { success = false, message = "Không tìm thấy đơn hàng" });
                
                var result = new
                {
                    success = true,
                    orderId = order.MaGiaoDich,
                    orderDate = order.NgayMua,
                    status = order.TrangThai,
                    paymentMethod = order.PhuongThucThanhToan,
                    
                    // Thông tin khách hàng
                    customerName = order.MaNguoiMuaNavigation.TenNguoiDung,
                    customerEmail = order.MaNguoiMuaNavigation.Email,
                    customerPhone = order.MaNguoiMuaNavigation.PhoneNumber,
                    shippingAddress = order.MaNguoiMuaNavigation.DiaChi,
                    
                    // Thông tin sản phẩm
                    artworkId = order.MaTranh,
                    artworkTitle = order.MaTranhNavigation.TieuDe,
                    artworkImage = order.MaTranhNavigation.DuongDanAnh,
                    artistName = order.MaTranhNavigation.MaNguoiDungNavigation.TenNguoiDung,
                    artworkPrice = order.MaTranhNavigation.Gia,
                    quantity = order.SoLuong,
                    totalAmount = order.SoTien
                };
                
                return Json(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

    }
}