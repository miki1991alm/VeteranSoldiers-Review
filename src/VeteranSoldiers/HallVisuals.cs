using System;
using System.Collections.Generic;
using UnityEngine;

namespace VeteranSoldiers;

/// <summary>Original low-poly architecture generated from primitives; no external game art is packaged.</summary>
internal static class HallVisuals
{
    private static Sprite? icon;
    internal static Sprite Icon => icon != null ? icon : icon = CreateIcon();
    private static readonly Color[] Palette =
    {
        new Color(.72f,.66f,.52f), // warm limestone
        new Color(.25f,.29f,.30f), // slate roof
        new Color(.30f,.17f,.10f), // aged oak
        new Color(.83f,.60f,.21f), // brass
        new Color(.15f,.32f,.38f), // veteran teal
        new Color(.12f,.12f,.10f), // recesses
        new Color(.34f,.40f,.42f), // lighter slate tiles
        new Color(.82f,.75f,.60f)  // carved sandstone
    };

    internal sealed class Model
    {
        internal Mesh Mesh = null!;
        internal Material[] Materials = null!;
    }

    internal static Model Create(float nativeWidth, float nativeLength, Material nativeMaterial)
    {
        if (nativeMaterial == null || nativeMaterial.shader == null) throw new InvalidOperationException("No supported Hall shader available.");
        var materials = new Material[Palette.Length];
        for (int i = 0; i < materials.Length; i++)
        {
            // Reuse the installed game's supported building shader (lighting, shadows,
            // placement tint and construction fill). Replace its albedo with original flat art.
            materials[i] = new Material(nativeMaterial) { name = "VeteranSoldiers_HallMaterial_" + i, enableInstancing = true };
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "VeteranSoldiers_HallPalette_" + i, filterMode = FilterMode.Point };
            texture.SetPixels(new[] { Palette[i], Palette[i], Palette[i], Palette[i] }); texture.Apply(false, true);
            bool textured = false;
            foreach (string property in new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo" })
                if (materials[i].HasProperty(property)) { materials[i].SetTexture(property, texture); textured = true; }
            if (!textured) throw new InvalidOperationException("The native building shader exposes no supported albedo texture.");
            if (materials[i].HasProperty("_Color")) materials[i].SetColor("_Color", Color.white);
            if (materials[i].HasProperty("_BaseColor")) materials[i].SetColor("_BaseColor", Color.white);
            if (materials[i].HasProperty("_Glossiness")) materials[i].SetFloat("_Glossiness", i == 3 ? .35f : .08f);
            if (materials[i].HasProperty("_Metallic")) materials[i].SetFloat("_Metallic", i == 3 ? .5f : 0);
        }
        float width = Math.Max(2, nativeWidth) * .86f;
        float length = Math.Max(2, nativeLength) * .86f;
        var geometry = new Geometry();
        float height = Math.Max(2.2f, width * .58f);
        // All detail uses the donor's scale: fixed centimetre-wide trim disappeared
        // against the game's thirty-unit buildings. Keep the complete model in its lot.
        geometry.Box(new Vector3(0,height*.04f,0), new Vector3(width*.96f,height*.08f,length*.92f), 0);
        geometry.Box(new Vector3(0,height*.13f,0), new Vector3(width*.82f,height*.18f,length*.76f), 7);
        geometry.Box(new Vector3(0,height*.43f,0), new Vector3(width*.76f,height*.60f,length*.70f), 0);
        geometry.Box(new Vector3(0,height*.23f,0), new Vector3(width*.80f,height*.055f,length*.74f), 7);
        geometry.Box(new Vector3(0,height*.72f,0), new Vector3(width*.80f,height*.07f,length*.74f), 2);
        geometry.Box(new Vector3(0,height*.77f,0), new Vector3(width*.84f,height*.045f,length*.78f), 7);

        foreach (float side in new[] { -1f, 1f })
        {
            // Four bays and deep framed windows make both long elevations readable.
            for (int bay = 0; bay < 4; bay++)
            {
                float z = (bay-1.5f)*length*.22f;
                geometry.Box(new Vector3(side*width*.385f,height*.47f,z), new Vector3(width*.035f,height*.49f,length*.032f), 2);
                geometry.Box(new Vector3(side*width*.393f,height*.27f,z), new Vector3(width*.07f,height*.14f,length*.065f), 7);
            }
            for (int window = 0; window < 3; window++)
            {
                float z = (window-1)*length*.22f;
                geometry.Box(new Vector3(side*width*.388f,height*.49f,z), new Vector3(width*.025f,height*.28f,length*.115f), 7);
                geometry.Box(new Vector3(side*width*.404f,height*.49f,z), new Vector3(width*.012f,height*.23f,length*.084f), 5);
                geometry.Box(new Vector3(side*width*.412f,height*.49f,z), new Vector3(width*.012f,height*.18f,length*.062f), 3);
                geometry.Box(new Vector3(side*width*.422f,height*.49f,z), new Vector3(width*.012f,height*.23f,length*.012f), 2);
                geometry.Box(new Vector3(side*width*.422f,height*.49f,z), new Vector3(width*.012f,height*.018f,length*.086f), 2);
                geometry.Box(new Vector3(side*width*.42f,height*.345f,z), new Vector3(width*.07f,height*.035f,length*.14f), 7);
            }
            // Stone corner buttresses and timber braces distinguish the Hall from a box.
            foreach (float end in new[] { -1f, 1f })
            {
                geometry.Box(new Vector3(side*width*.38f,height*.40f,end*length*.335f), new Vector3(width*.065f,height*.67f,length*.07f), 7);
                geometry.Box(new Vector3(side*width*.38f,height*.12f,end*length*.335f), new Vector3(width*.10f,height*.12f,length*.11f), 0);
                geometry.Beam(new Vector3(side*width*.386f,height*.60f,end*length*.22f), new Vector3(side*width*.386f,height*.71f,end*length*.32f), width*.018f,2);
            }
            // Front and rear both have heraldry; the default isometric camera sees the rear.
            foreach (float end in new[] { -1f, 1f })
            {
                float z = end*length*.366f;
                geometry.Box(new Vector3(side*width*.245f,height*.49f,z), new Vector3(width*.115f,height*.31f,length*.012f),4);
                geometry.Box(new Vector3(side*width*.245f,height*.655f,z+end*length*.012f),new Vector3(width*.15f,height*.025f,length*.035f),3);
                geometry.Shield(new Vector3(side*width*.245f,height*.50f,z+end*length*.017f),width*.052f,height*.135f,length*.012f,3);
                geometry.Box(new Vector3(side*width*.245f,height*.50f,z+end*length*.026f),new Vector3(width*.012f,height*.075f,length*.006f),4);
            }
        }
        // Recessed double oak doors on the entry and a smaller rear commemorative panel.
        foreach (float end in new[] { -1f, 1f })
        {
            geometry.Box(new Vector3(0,height*.39f,end*length*.356f), new Vector3(width*.24f,height*.47f,length*.025f),7);
            geometry.Box(new Vector3(0,height*.36f,end*length*.376f), new Vector3(width*.19f,height*.40f,length*.018f),5);
            for(int plank=0;plank<6;plank++)
                geometry.Box(new Vector3((plank-2.5f)*width*.028f,height*.36f,end*length*.39f),new Vector3(width*.026f,height*.38f,length*.015f),2);
            foreach(float band in new[]{.24f,.45f})
                geometry.Box(new Vector3(0,height*band,end*length*.403f),new Vector3(width*.18f,height*.025f,length*.012f),3);
        }
        // A projecting entry porch and substantial steps read clearly at normal RTS zoom.
        geometry.Box(new Vector3(0,height*.075f,-length*.46f),new Vector3(width*.38f,height*.15f,length*.22f),7);
        for(int step=0;step<3;step++)
            geometry.Box(new Vector3(0,height*(.025f+step*.025f),-length*(.555f-step*.035f)),new Vector3(width*(.43f-step*.035f),height*(.05f+step*.05f),length*.065f),0);
        foreach(float side in new[]{-1f,1f})
        {
            geometry.Box(new Vector3(side*width*.16f,height*.39f,-length*.48f),new Vector3(width*.045f,height*.58f,length*.045f),2);
            geometry.Box(new Vector3(side*width*.16f,height*.15f,-length*.48f),new Vector3(width*.075f,height*.13f,length*.075f),7);
        }
        geometry.Box(new Vector3(0,height*.68f,-length*.44f),new Vector3(width*.39f,height*.04f,length*.24f),2);
        geometry.OffsetRoof(new Vector3(0,0,-length*.44f),width*.22f,length*.145f,height*.69f,height*.86f,1);

        // Individual alternating slate courses add visible shape, not a flat roof texture.
        geometry.Roof(width*.46f,length*.405f,height*.79f,height*1.10f,5);
        geometry.SlateRoof(width*.46f,length*.405f,height*.795f,height*1.105f,6,10);
        foreach(float side in new[]{-1f,1f})
            geometry.Box(new Vector3(side*width*.455f,height*.79f,0),new Vector3(width*.025f,height*.045f,length*.83f),2);
        foreach(float end in new[]{-1f,1f})
        {
            geometry.Beam(new Vector3(-width*.466f,height*.79f,end*length*.411f),new Vector3(0,height*1.11f,end*length*.411f),width*.023f,7);
            geometry.Beam(new Vector3(width*.466f,height*.79f,end*length*.411f),new Vector3(0,height*1.11f,end*length*.411f),width*.023f,7);
            geometry.Box(new Vector3(0,height*.925f,end*length*.414f),new Vector3(width*.018f,height*.31f,length*.022f),2);
        }
        geometry.Box(new Vector3(0,height*1.11f,0),new Vector3(width*.035f,height*.035f,length*.83f),3);

        // Raised open lantern: four brass windows, corner columns and a clearly visible flag.
        float towerZ=length*.10f;
        geometry.Box(new Vector3(0,height*1.17f,towerZ),new Vector3(width*.22f,height*.18f,length*.21f),7);
        geometry.Box(new Vector3(0,height*1.285f,towerZ),new Vector3(width*.25f,height*.05f,length*.24f),2);
        geometry.Box(new Vector3(0,height*1.43f,towerZ),new Vector3(width*.18f,height*.25f,length*.17f),5);
        foreach(float side in new[]{-1f,1f})
        {
            geometry.Box(new Vector3(side*width*.095f,height*1.43f,towerZ),new Vector3(width*.015f,height*.18f,length*.12f),3);
            geometry.Box(new Vector3(0,height*1.43f,towerZ+side*length*.09f),new Vector3(width*.13f,height*.18f,length*.015f),3);
            foreach(float end in new[]{-1f,1f})
                geometry.Box(new Vector3(side*width*.105f,height*1.435f,towerZ+end*length*.10f),new Vector3(width*.035f,height*.30f,length*.035f),7);
        }
        geometry.Box(new Vector3(0,height*1.59f,towerZ),new Vector3(width*.27f,height*.04f,length*.26f),2);
        geometry.Pyramid(new Vector3(0,height*1.61f,towerZ),width*.16f,length*.15f,height*.22f,1);
        geometry.Box(new Vector3(0,height*1.89f,towerZ),new Vector3(width*.014f,height*.20f,length*.014f),3);
        geometry.Box(new Vector3(width*.075f,height*1.94f,towerZ),new Vector3(width*.15f,height*.095f,length*.012f),4);
        geometry.Box(new Vector3(width*.075f,height*1.94f,towerZ),new Vector3(width*.025f,height*.095f,length*.018f),3);
        foreach(float side in new[]{-1f,1f})
        {
            geometry.Box(new Vector3(side*width*.37f,height*.12f,-length*.47f),new Vector3(width*.105f,height*.24f,length*.105f),7);
            geometry.Pyramid(new Vector3(side*width*.37f,height*.24f,-length*.47f),width*.06f,length*.06f,height*.12f,3);
        }
        return new Model { Mesh = geometry.Build(), Materials = materials };
    }

    private sealed class Geometry
    {
        private readonly List<Vector3> vertices = new List<Vector3>();
        private readonly List<Vector2> uv = new List<Vector2>();
        private readonly List<int>[] indices = new List<int>[Palette.Length];
        internal Geometry() { for(int i=0;i<indices.Length;i++) indices[i]=new List<int>(); }
        private void Quad(Vector3 a,Vector3 b,Vector3 c,Vector3 d,int material)
        {
            int start=vertices.Count; vertices.AddRange(new[]{a,b,c,d});
            uv.AddRange(new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(1,1),new Vector2(0,1)});
            indices[material].AddRange(new[]{start,start+1,start+2,start,start+2,start+3});
        }
        private void Triangle(Vector3 a,Vector3 b,Vector3 c,int material)
        {
            int start=vertices.Count; vertices.AddRange(new[]{a,b,c});
            uv.AddRange(new[]{new Vector2(0,0),new Vector2(1,0),new Vector2(.5f,1)});
            indices[material].AddRange(new[]{start,start+1,start+2});
        }
        internal void Box(Vector3 center,Vector3 size,int material)
        {
            Vector3 n=center-size*.5f,p=center+size*.5f;
            Quad(new Vector3(n.x,n.y,n.z),new Vector3(n.x,p.y,n.z),new Vector3(p.x,p.y,n.z),new Vector3(p.x,n.y,n.z),material);
            Quad(new Vector3(p.x,n.y,p.z),new Vector3(p.x,p.y,p.z),new Vector3(n.x,p.y,p.z),new Vector3(n.x,n.y,p.z),material);
            Quad(new Vector3(n.x,n.y,p.z),new Vector3(n.x,p.y,p.z),new Vector3(n.x,p.y,n.z),new Vector3(n.x,n.y,n.z),material);
            Quad(new Vector3(p.x,n.y,n.z),new Vector3(p.x,p.y,n.z),new Vector3(p.x,p.y,p.z),new Vector3(p.x,n.y,p.z),material);
            Quad(new Vector3(n.x,p.y,n.z),new Vector3(n.x,p.y,p.z),new Vector3(p.x,p.y,p.z),new Vector3(p.x,p.y,n.z),material);
            Quad(new Vector3(n.x,n.y,p.z),new Vector3(n.x,n.y,n.z),new Vector3(p.x,n.y,n.z),new Vector3(p.x,n.y,p.z),material);
        }
        internal void Beam(Vector3 start,Vector3 end,float thickness,int material)
        {
            Vector3 direction=end-start;
            if(direction.sqrMagnitude<.0001f)return;
            Quaternion rotation=Quaternion.FromToRotation(Vector3.up,direction.normalized);
            Vector3 center=(start+end)*.5f;
            float x=thickness*.5f,y=direction.magnitude*.5f,z=x;
            Vector3 P(float a,float b,float c)=>center+rotation*new Vector3(a,b,c);
            Quad(P(-x,-y,-z),P(-x,y,-z),P(x,y,-z),P(x,-y,-z),material);
            Quad(P(x,-y,z),P(x,y,z),P(-x,y,z),P(-x,-y,z),material);
            Quad(P(-x,-y,z),P(-x,y,z),P(-x,y,-z),P(-x,-y,-z),material);
            Quad(P(x,-y,-z),P(x,y,-z),P(x,y,z),P(x,-y,z),material);
            Quad(P(-x,y,-z),P(-x,y,z),P(x,y,z),P(x,y,-z),material);
            Quad(P(-x,-y,z),P(-x,-y,-z),P(x,-y,-z),P(x,-y,z),material);
        }
        internal void Shield(Vector3 center,float width,float height,float depth,int material)
        {
            var outline=new[]{new Vector2(-width*.5f,height*.5f),new Vector2(width*.5f,height*.5f),new Vector2(width*.5f,-height*.12f),new Vector2(0,-height*.5f),new Vector2(-width*.5f,-height*.12f)};
            Vector3 P(int i,float z)=>center+new Vector3(outline[i].x,outline[i].y,z);
            for(int i=1;i<outline.Length-1;i++)
            {
                Triangle(P(0,-depth*.5f),P(i,-depth*.5f),P(i+1,-depth*.5f),material);
                Triangle(P(0,depth*.5f),P(i+1,depth*.5f),P(i,depth*.5f),material);
            }
            for(int i=0;i<outline.Length;i++)
            { int next=(i+1)%outline.Length; Quad(P(i,-depth*.5f),P(i,depth*.5f),P(next,depth*.5f),P(next,-depth*.5f),material); }
        }
        internal void OffsetRoof(Vector3 offset,float x,float z,float bottom,float top,int material)
        {
            Vector3 P(float a,float b,float c)=>offset+new Vector3(a,b,c);
            Quad(P(-x,bottom,z),P(0,top,z),P(0,top,-z),P(-x,bottom,-z),material);
            Quad(P(x,bottom,-z),P(0,top,-z),P(0,top,z),P(x,bottom,z),material);
            Triangle(P(0,top,-z),P(x,bottom,-z),P(-x,bottom,-z),material);
            Triangle(P(0,top,z),P(-x,bottom,z),P(x,bottom,z),material);
        }
        internal void SlateRoof(float x,float z,float bottom,float top,int rows,int columns)
        {
            float stepX=x/rows,stepZ=2*z/columns;
            for(int row=0;row<rows;row++)for(int column=0;column<columns;column++)
            {
                float inner=row*stepX+stepX*.012f,outer=(row+1)*stepX-stepX*.012f;
                float z0=-z+column*stepZ+stepZ*.018f,z1=z0+stepZ*.964f;
                float low=top-(top-bottom)*outer/x,high=top-(top-bottom)*inner/x;
                int material=(row+column*3)%4==0?6:1;
                Quad(new Vector3(outer,low,z0),new Vector3(inner,high,z0),new Vector3(inner,high,z1),new Vector3(outer,low,z1),material);
                Quad(new Vector3(-outer,low,z1),new Vector3(-inner,high,z1),new Vector3(-inner,high,z0),new Vector3(-outer,low,z0),material);
            }
        }
        internal void Roof(float x,float z,float bottom,float top,int material)
        {
            Quad(new Vector3(-x,bottom,z),new Vector3(0,top,z),new Vector3(0,top,-z),new Vector3(-x,bottom,-z),material);
            Quad(new Vector3(x,bottom,-z),new Vector3(0,top,-z),new Vector3(0,top,z),new Vector3(x,bottom,z),material);
            Triangle(new Vector3(0,top,-z),new Vector3(x,bottom,-z),new Vector3(-x,bottom,-z),material);
            Triangle(new Vector3(0,top,z),new Vector3(-x,bottom,z),new Vector3(x,bottom,z),material);
        }
        internal void Pyramid(Vector3 center,float x,float z,float height,int material)
        {
            Vector3 top=center+new Vector3(0,height,0);
            Vector3 a=center+new Vector3(-x,0,-z),b=center+new Vector3(x,0,-z),c=center+new Vector3(x,0,z),d=center+new Vector3(-x,0,z);
            Triangle(a,top,b,material); Triangle(b,top,c,material); Triangle(c,top,d,material); Triangle(d,top,a,material);
        }
        internal Mesh Build()
        {
            var mesh=new Mesh { name="VeteranSoldiers_OriginalHall",subMeshCount=indices.Length };
            mesh.SetVertices(vertices); mesh.SetUVs(0,uv);
            var colors = new List<Color>(vertices.Count); for (int i = 0; i < vertices.Count; i++) colors.Add(Color.white); mesh.SetColors(colors);
            for(int i=0;i<indices.Length;i++) mesh.SetTriangles(indices[i],i);
            mesh.RecalculateNormals(); mesh.RecalculateBounds(); return mesh;
        }
    }

    private static Sprite CreateIcon()
    {
        const int size=96;
        var pixels=new Color32[size*size];
        Color32 stone=new Color(.96f,.86f,.65f),gold=new Color(.93f,.64f,.20f),shade=new Color(.24f,.31f,.31f);
        void Fill(int x0,int y0,int x1,int y1,Color32 color)
        { for(int y=Math.Max(0,y0);y<Math.Min(size,y1);y++) for(int x=Math.Max(0,x0);x<Math.Min(size,x1);x++) pixels[y*size+x]=color; }
        Fill(15,18,81,25,gold); Fill(20,25,76,59,stone); Fill(40,25,56,45,shade);
        Fill(27,31,33,49,shade); Fill(63,31,69,49,shade);
        for(int y=59;y<77;y++) { int inset=(y-59)*2; Fill(12+inset,y,84-inset,y+1,gold); }
        Fill(43,69,53,86,stone); Fill(47,84,50,94,gold); Fill(50,87,64,93,gold);
        Fill(9,13,87,18,stone);
        var texture=new Texture2D(size,size,TextureFormat.RGBA32,false) {name="VeteranSoldiers_HallIcon",filterMode=FilterMode.Bilinear};
        texture.SetPixels32(pixels); texture.Apply(false,true);
        return Sprite.Create(texture,new Rect(0,0,size,size),new Vector2(.5f,.5f),100);
    }
}
