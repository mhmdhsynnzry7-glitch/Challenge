export type UserRole = 'Admin' | 'Sales';
export type OrderStatus = 'Confirmed' | 'Cancelled';
export type OrderSort = 'orderDate' | 'orderNumber' | 'totalAmount';

export interface LoginUser { id: number; username: string; role: UserRole; }
export interface LoginResponse { token: string; expiresAt: string; user: LoginUser; }
export interface Page<T> { items: T[]; totalCount: number; page: number; pageSize: number; }
export interface Product {
  id: number; code: string; name: string; price: number; stockQuantity: number; isActive: boolean;
}
export interface Customer { id: number; code: string; name: string; }
export interface ProductQuery { search?: string; isActive?: boolean; page: number; pageSize: number; }
export interface OrderQuery {
  orderNumber?: string; customerId?: number | null; status?: OrderStatus | '';
  fromDate?: string; toDate?: string; sortBy: OrderSort; sortDirection: 'asc' | 'desc';
  page: number; pageSize: number;
}
export interface OrderSummary {
  id: number; orderNumber: string; customerId: number; customerName: string;
  orderDate: string; status: OrderStatus; totalAmount: number;
}
export interface OrderItem { productId: number; quantity: number; unitPrice: number; totalPrice: number; }
export interface CreateOrderRequest { customerId: number; items: { productId: number; quantity: number }[]; }
export interface CreateOrderResponse {
  id: number; orderNumber: string; customerId: number; orderDate: string;
  status: OrderStatus; totalAmount: number; createdBy: number; items: OrderItem[];
}
export interface OrderDetails extends Omit<CreateOrderResponse, 'items'> {
  customer: Customer;
  createdByUser: { id: number; username: string };
  items: (OrderItem & { productCode: string; productName: string })[];
}
