#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <new>

template<class T> void drop(T*& p) { if(p) { p->Release(); p=nullptr; } }
struct Capture {
 ID3D11Device* device=nullptr; ID3D11DeviceContext* context=nullptr;
 IDXGIOutputDuplication* duplication=nullptr; ID3D11Texture2D* staging=nullptr;
 DXGI_MODE_ROTATION rotation=DXGI_MODE_ROTATION_IDENTITY;
 ~Capture() { drop(staging); drop(duplication); drop(context); drop(device); }
};
extern "C" __declspec(dllexport) void capture_close(Capture* c) { delete c; }
extern "C" __declspec(dllexport) HRESULT capture_open(const wchar_t* name,Capture** result) {
 *result=nullptr; IDXGIFactory1* factory=nullptr;
 HRESULT hr=CreateDXGIFactory1(__uuidof(IDXGIFactory1),(void**)&factory);
 if(FAILED(hr)) return hr;
 for(UINT a=0;;a++) {
  IDXGIAdapter1* adapter=nullptr; if(factory->EnumAdapters1(a,&adapter)==DXGI_ERROR_NOT_FOUND) break;
  for(UINT o=0;;o++) {
   IDXGIOutput* output=nullptr; if(adapter->EnumOutputs(o,&output)==DXGI_ERROR_NOT_FOUND) break;
   DXGI_OUTPUT_DESC desc{}; output->GetDesc(&desc);
   if(desc.AttachedToDesktop && wcscmp(name,desc.DeviceName)==0) {
    Capture* c=new(std::nothrow) Capture(); if(!c) {drop(output);drop(adapter);drop(factory);return E_OUTOFMEMORY;}
    c->rotation=desc.Rotation;
    hr=D3D11CreateDevice(adapter,D3D_DRIVER_TYPE_UNKNOWN,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,nullptr,0,D3D11_SDK_VERSION,&c->device,nullptr,&c->context);
    IDXGIOutput1* out1=nullptr;
    if(SUCCEEDED(hr)) hr=output->QueryInterface(__uuidof(IDXGIOutput1),(void**)&out1);
    if(SUCCEEDED(hr)) hr=out1->DuplicateOutput(c->device,&c->duplication);
    drop(out1);drop(output);drop(adapter);drop(factory);
    if(FAILED(hr)) {delete c; return hr;} *result=c; return S_OK;
   }
   drop(output);
  }
  drop(adapter);
 }
 drop(factory); return DXGI_ERROR_NOT_FOUND;
}
// Returns S_FALSE for an unchanged desktop. Output is an upright 192 x 108 RGB sample grid.
extern "C" __declspec(dllexport) HRESULT capture_read(Capture* c,unsigned char* rgb,int width,int height) {
 if(!c || !rgb || width<1 || height<1) return E_INVALIDARG;
 DXGI_OUTDUPL_FRAME_INFO info{}; IDXGIResource* resource=nullptr;
 HRESULT hr=c->duplication->AcquireNextFrame(0,&info,&resource);
 if(hr==DXGI_ERROR_WAIT_TIMEOUT) return S_FALSE;
 if(FAILED(hr)) return hr;
 ID3D11Texture2D* frame=nullptr; hr=resource->QueryInterface(__uuidof(ID3D11Texture2D),(void**)&frame);drop(resource);
 if(SUCCEEDED(hr)) {
  D3D11_TEXTURE2D_DESC desc{};frame->GetDesc(&desc);
  if(!c->staging) {
   desc.Usage=D3D11_USAGE_STAGING;desc.BindFlags=0;desc.CPUAccessFlags=D3D11_CPU_ACCESS_READ;desc.MiscFlags=0;
   hr=c->device->CreateTexture2D(&desc,nullptr,&c->staging);
  }
  if(SUCCEEDED(hr)) {
   c->context->CopyResource(c->staging,frame);D3D11_MAPPED_SUBRESOURCE map{};
   hr=c->context->Map(c->staging,0,D3D11_MAP_READ,0,&map);
   if(SUCCEEDED(hr)) {
    for(int y=0;y<height;y++) for(int x=0;x<width;x++) {
     double u=(x+.5)/width,v=(y+.5)/height,su=u,sv=v;
     switch(c->rotation) {
      case DXGI_MODE_ROTATION_ROTATE90:su=v;sv=1-u;break;
      case DXGI_MODE_ROTATION_ROTATE180:su=1-u;sv=1-v;break;
      case DXGI_MODE_ROTATION_ROTATE270:su=1-v;sv=u;break;
      default:break;
     }
     UINT px=(UINT)(su*desc.Width),py=(UINT)(sv*desc.Height);
     if(px>=desc.Width)px=desc.Width-1;if(py>=desc.Height)py=desc.Height-1;
     auto p=(unsigned char*)map.pData+py*map.RowPitch+px*4;auto d=rgb+(y*width+x)*3;
     d[0]=p[2];d[1]=p[1];d[2]=p[0];
    }
    c->context->Unmap(c->staging,0);
   }
  }
 }
 drop(frame);c->duplication->ReleaseFrame();return hr;
}
